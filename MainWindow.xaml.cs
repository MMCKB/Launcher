using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using WinRT.Interop;
using Windows.Foundation;
using Windows.UI;

namespace WinUI3Desktop;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, System.Text.StringBuilder pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int LeftWidth;
        public int RightWidth;
        public int TopHeight;
        public int BottomHeight;
    }

    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int WsBorder = 0x00800000;
    private const int WsDlgFrame = 0x00400000;
    private const int WsThickFrame = 0x00040000;
    private const int WsExClientEdge = 0x00000200;
    private const int WsExWindowEdge = 0x00000100;
    private const int WsExStaticEdge = 0x00020000;
    private const int DwmwaCornerPreference = 33;
    private const int DwmPreferRound = 2;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const uint SpiGetDeskWallpaper = 0x0073;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpFrameChanged = 0x0020;
    private const int MaxPath = 260;
    private const int DockScaleAnimationMs = 90;

    private static readonly IntPtr HwndTopmost = new(-1);
    private readonly DispatcherQueueTimer _hintTimer;
    private readonly List<Button> _dockButtons;

    public MainWindow()
    {
        InitializeComponent();

        _dockButtons = new List<Button>
        {
            LauncherDockButton,
            BrowserDockButton,
            FilesDockButton,
            NotesDockButton,
            MusicDockButton,
            SettingsDockButton,
            RecycleDockButton
        };

        _hintTimer = DispatcherQueue.CreateTimer();
        _hintTimer.Interval = TimeSpan.FromSeconds(2.2);
        _hintTimer.Tick += (_, _) =>
        {
            DockHint.Visibility = Visibility.Collapsed;
            _hintTimer.Stop();
        };

        ConfigureShellWindow();
        ReloadWallpaper();
        RootGrid.PointerPressed += RootGrid_PointerPressed;
    }

    private void ConfigureShellWindow()
    {
        var hWnd = WindowNative.GetWindowHandle(this);

        var style = GetWindowLong(hWnd, GwlStyle);
        style &= ~WsBorder;
        style &= ~WsDlgFrame;
        style &= ~WsThickFrame;
        SetWindowLong(hWnd, GwlStyle, style);

        var exStyle = GetWindowLong(hWnd, GwlExStyle);
        exStyle &= ~WsExClientEdge;
        exStyle &= ~WsExWindowEdge;
        exStyle &= ~WsExStaticEdge;
        SetWindowLong(hWnd, GwlExStyle, exStyle);

        var margins = new Margins { LeftWidth = -1, RightWidth = -1, TopHeight = -1, BottomHeight = -1 };
        DwmExtendFrameIntoClientArea(hWnd, ref margins);

        var cornerPreference = DwmPreferRound;
        DwmSetWindowAttribute(hWnd, DwmwaCornerPreference, ref cornerPreference, sizeof(int));

        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        var screenWidth = GetSystemMetrics(SmCxScreen);
        var screenHeight = GetSystemMetrics(SmCyScreen);

        appWindow.MoveAndResize(new Windows.Graphics.RectInt32(0, 0, screenWidth, screenHeight));
        SetWindowPos(hWnd, HwndTopmost, 0, 0, screenWidth, screenHeight, SwpShowWindow | SwpFrameChanged);

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(false, false);
        }

        appWindow.TitleBar.ExtendsContentIntoTitleBar = false;
        appWindow.Title = "MyDock";
    }

    private void DockHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (DockPanel.ActualWidth <= 0)
        {
            return;
        }

        var pointerPosition = e.GetCurrentPoint(DockPanel).Position;

        foreach (var button in _dockButtons)
        {
            var center = button.TransformToVisual(DockPanel)
                .TransformPoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
            var distance = Math.Abs(pointerPosition.X - center.X);
            var influence = Math.Max(0, 1 - (distance / 150.0));
            var scale = 1.0 + (0.36 * influence * influence);
            AnimateDockButton(button, (float)scale);
        }
    }

    private void DockHost_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        ResetDockScale();
    }

    private static void AnimateDockButton(Button button, float scale)
    {
        var visual = ElementCompositionPreview.GetElementVisual(button);
        visual.CenterPoint = new Vector3((float)(button.ActualWidth / 2), (float)button.ActualHeight, 0);

        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1f, new Vector3(scale, scale, 1f));
        animation.Duration = TimeSpan.FromMilliseconds(DockScaleAnimationMs);
        visual.StartAnimation("Scale", animation);
    }

    private void ResetDockScale()
    {
        foreach (var button in _dockButtons)
        {
            AnimateDockButton(button, 1f);
        }
    }

    private void DockButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        var appName = button.Tag?.ToString() ?? "应用";
        ShowHint($"{appName} 已启动");
    }

    private void DockButton_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        e.Handled = true;
        var appName = button.Tag?.ToString() ?? "应用";
        CreateAppContextMenu(appName).ShowAt(button, new FlyoutShowOptions
        {
            Position = e.GetPosition(button)
        });
    }

    private MenuFlyout CreateAppContextMenu(string appName)
    {
        var menu = new MenuFlyout();

        var appHeader = new MenuFlyoutItem
        {
            Text = $"{appName}  ·  正在运行",
            Icon = CreateIcon("\uE77B"),
            IsEnabled = false
        };
        menu.Items.Add(appHeader);
        menu.Items.Add(new MenuFlyoutSeparator());

        menu.Items.Add(CreateMenuItem("打开", "\uE8A7", () => ShowHint($"正在打开 {appName}")));
        menu.Items.Add(CreateMenuItem("新建窗口", "\uE8A7", () => ShowHint($"已为 {appName} 请求新窗口")));
        menu.Items.Add(CreateMenuItem("从 Dock 移除", "\uE71B", () => ShowHint($"{appName} 已从 Dock 移除（演示）")));
        menu.Items.Add(new MenuFlyoutSeparator());

        var appearance = new MenuFlyoutSubItem
        {
            Text = "Dock 外观",
            Icon = CreateIcon("\uE790")
        };
        appearance.Items.Add(CreateMenuItem("图标大小：中", "\uE8A3", () => ShowHint("图标大小已设为：中")));
        appearance.Items.Add(CreateMenuItem("自动隐藏：关闭", "\uE708", () => ShowHint("自动隐藏：关闭")));
        menu.Items.Add(appearance);

        var material = new MenuFlyoutSubItem
        {
            Text = "材质：晶体玻璃",
            Icon = CreateIcon("\uECA5")
        };
        material.Items.Add(CreateMenuItem("晶体玻璃", "\uE73E", () => ApplyDockMaterial("晶体玻璃")));
        material.Items.Add(CreateMenuItem("柔雾云母", "\uE734", () => ApplyDockMaterial("柔雾云母")));
        material.Items.Add(CreateMenuItem("深色玻璃", "\uE7C3", () => ApplyDockMaterial("深色玻璃")));
        material.Items.Add(CreateMenuItem("纯净模式", "\uE771", () => ApplyDockMaterial("纯净模式")));
        menu.Items.Add(material);

        var position = new MenuFlyoutSubItem
        {
            Text = "位置：底部",
            Icon = CreateIcon("\uE7F4")
        };
        position.Items.Add(CreateMenuItem("底部", "\uE74A", () => ShowHint("Dock 位置：底部")));
        position.Items.Add(CreateMenuItem("左侧", "\uE76B", () => ShowHint("Dock 位置：左侧（演示）")));
        position.Items.Add(CreateMenuItem("右侧", "\uE76C", () => ShowHint("Dock 位置：右侧（演示）")));
        menu.Items.Add(position);
        menu.Items.Add(new MenuFlyoutSeparator());

        menu.Items.Add(CreateMenuItem("隐藏 Dock", "\uE708", () => ToggleDockVisibility()));
        menu.Items.Add(CreateMenuItem("打开设置", "\uE713", () => ShowHint("设置页面将在下一步接入")));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CreateMenuItem("退出 MyDock", "\uE7E8", () => Application.Current.Exit()));

        return menu;
    }

    private static FontIcon CreateIcon(string glyph) => new()
    {
        Glyph = glyph,
        FontSize = 16
    };

    private static MenuFlyoutItem CreateMenuItem(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            Icon = CreateIcon(glyph)
        };
        item.Click += (_, _) => action();
        return item;
    }

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsDockElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var point = e.GetCurrentPoint(RootGrid);
        if (!point.Properties.IsRightButtonPressed)
        {
            return;
        }

        e.Handled = true;
        var menu = new MenuFlyout();
        menu.Items.Add(CreateMenuItem("打开设置", "\uE713", () => ShowHint("设置页面将在下一步接入")));
        menu.Items.Add(CreateMenuItem("刷新壁纸", "\uE72C", ReloadWallpaper));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CreateMenuItem("退出 MyDock", "\uE7E8", () => Application.Current.Exit()));
        var position = point.Position;
        menu.ShowAt(RootGrid, new FlyoutShowOptions
        {
            Position = new Point(position.X, position.Y)
        });
    }

    private bool IsDockElement(DependencyObject? element)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, DockHost))
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private void ToggleDockVisibility()
    {
        DockHost.Visibility = DockHost.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        ShowHint(DockHost.Visibility == Visibility.Visible ? "Dock 已显示" : "Dock 已隐藏" );
    }

    private void ApplyDockMaterial(string material)
    {
        var brush = material switch
        {
            "柔雾云母" => new SolidColorBrush(Color.FromArgb(235, 39, 50, 73)),
            "深色玻璃" => new AcrylicBrush
            {
                BackgroundSource = AcrylicBackgroundSource.Backdrop,
                TintColor = Color.FromArgb(255, 8, 14, 28),
                TintOpacity = 0.88,
                FallbackColor = Color.FromArgb(238, 8, 14, 28)
            },
            "纯净模式" => new SolidColorBrush(Color.FromArgb(245, 27, 39, 62)),
            _ => new AcrylicBrush
            {
                BackgroundSource = AcrylicBackgroundSource.Backdrop,
                TintColor = Color.FromArgb(255, 23, 36, 60),
                TintOpacity = 0.76,
                FallbackColor = Color.FromArgb(232, 23, 36, 60)
            }
        };

        DockSurface.Background = brush;
        ShowHint($"Dock 材质：{material}");
    }

    private void ShowHint(string message)
    {
        DockHintText.Text = message;
        DockHint.Visibility = Visibility.Visible;
        _hintTimer.Stop();
        _hintTimer.Start();
    }

    private void ReloadWallpaper()
    {
        try
        {
            var wallpaperPath = new System.Text.StringBuilder(MaxPath);
            if (!SystemParametersInfo(SpiGetDeskWallpaper, (uint)MaxPath, wallpaperPath, 0))
            {
                return;
            }

            var path = wallpaperPath.ToString();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            var bitmap = new BitmapImage { UriSource = new Uri(path) };
            WallpaperImage.Source = bitmap;
        }
        catch
        {
            WallpaperImage.Source = null;
        }
    }
}
