using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using WinRT.Interop;
using Windows.UI;

namespace WinUI3Desktop;

public sealed partial class MainWindow : Window
{
    // Win32 Interop
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, System.Text.StringBuilder pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int leftWidth;
        public int rightWidth;
        public int topHeight;
        public int bottomHeight;
    }

    // Constants
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_BORDER = 0x00800000;
    private const int WS_DLGFRAME = 0x00400000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_EX_CLIENTEDGE = 0x00000200;
    private const int WS_EX_WINDOWEDGE = 0x00000100;
    private const int WS_EX_STATICEDGE = 0x00020000;
    private const int DWMWA_CORNER_PREFERENCE = 33;
    private const int DWM_PREFER_ROUND_DWMWCP_ROUND = 2;

    private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const uint SPI_GETDESKWALLPAPER = 0x0073;
    private const int MAX_PATH = 260;

    // Configurable properties
    public int MenuCornerRadius { get; set; } = 12;
    public int WindowCornerRadius { get; set; } = 0;

    // Animation durations (120-180ms for menu, 80-120ms for hover)
    private const int MenuOpenCloseAnimationMs = 150;
    private const int MenuItemHoverAnimationMs = 100;

    // Context menu
    private MenuFlyout? _contextMenu;

    public MainWindow()
    {
        this.InitializeComponent();

        // Get handle for this window
        var hWnd = WindowNative.GetWindowHandle(this);

        // Remove all window borders and edges
        int style = GetWindowLong(hWnd, GWL_STYLE);
        style &= ~WS_BORDER;
        style &= ~WS_DLGFRAME;
        style &= ~WS_THICKFRAME;
        SetWindowLong(hWnd, GWL_STYLE, style);

        int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
        exStyle &= ~WS_EX_CLIENTEDGE;
        exStyle &= ~WS_EX_WINDOWEDGE;
        exStyle &= ~WS_EX_STATICEDGE;
        SetWindowLong(hWnd, GWL_EXSTYLE, exStyle);

        // Extend frame into client area to remove white borders
        var margins = new MARGINS { leftWidth = -1, rightWidth = -1, topHeight = -1, bottomHeight = -1 };
        DwmExtendFrameIntoClientArea(hWnd, ref margins);

        // Set window corner preference
        int cornerPreference = WindowCornerRadius > 0 ? DWM_PREFER_ROUND_DWMWCP_ROUND : 0;
        DwmSetWindowAttribute(hWnd, DWMWA_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));

        // Use AppWindow to configure the window
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        // Get full screen dimensions (covers taskbar)
        int screenWidth = GetSystemMetrics(SM_CXSCREEN);
        int screenHeight = GetSystemMetrics(SM_CYSCREEN);

        // Move and resize to cover the entire screen
        appWindow.MoveAndResize(new Windows.Graphics.RectInt32(0, 0, screenWidth, screenHeight));

        // Keep the desktop-sized window below all normal application windows.
        // SWP_NOACTIVATE prevents the background desktop from stealing foreground focus.
        SetWindowPos(hWnd, HWND_BOTTOM, 0, 0, screenWidth, screenHeight,
            SWP_SHOWWINDOW | SWP_FRAMECHANGED | SWP_NOACTIVATE);

        // Configure the presenter as a borderless, non-topmost desktop surface.
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        // Hide title bar
        appWindow.TitleBar.ExtendsContentIntoTitleBar = false;
        appWindow.Title = "";

        // Set system wallpaper as background
        SetWallpaperBackground(screenWidth, screenHeight);

        // Setup context menu and events
        InitializeContextMenu();
        AttachRightClickHandler();
    }

    private void InitializeContextMenu()
    {
        _contextMenu = new MenuFlyout();

        // Style the menu presenter
        _contextMenu.Opening += (s, e) =>
        {
            if (_contextMenu.Target is MenuFlyoutPresenter presenter)
            {
                presenter.Background = new AcrylicBrush
                {
                    FallbackColor = Color.FromArgb(200, 32, 32, 32),
                    TintColor = Color.FromArgb(180, 32, 32, 32),
                    TintOpacity = 0.8
                };
                presenter.CornerRadius = new CornerRadius(MenuCornerRadius);
                presenter.BorderThickness = new Thickness(1);
                presenter.BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
                presenter.Padding = new Thickness(8);
            }
        };

        // Add menu items
        var settingsItem = new MenuFlyoutItem
        {
            Text = "设置",
            Icon = new FontIcon { Glyph = "\uE713", FontSize = 16 }
        };
        settingsItem.Click += (s, e) => { /* TODO: Open settings */ };

        var refreshItem = new MenuFlyoutItem
        {
            Text = "刷新",
            Icon = new FontIcon { Glyph = "\uE72C", FontSize = 16 }
        };
        refreshItem.Click += (s, e) => ReloadWallpaper();

        var separator = new MenuFlyoutSeparator();

        var exitItem = new MenuFlyoutItem
        {
            Text = "退出",
            Icon = new FontIcon { Glyph = "\uE711", FontSize = 16 }
        };
        exitItem.Click += (s, e) => Application.Current.Exit();

        _contextMenu.Items.Add(settingsItem);
        _contextMenu.Items.Add(refreshItem);
        _contextMenu.Items.Add(separator);
        _contextMenu.Items.Add(exitItem);

        // Setup animations
        _contextMenu.Opening += (s, e) => ApplyOpenAnimation();
        _contextMenu.Closing += (s, e) => ApplyCloseAnimation();

        // Setup hover animations
        SetupHoverAnimations(settingsItem);
        SetupHoverAnimations(refreshItem);
        SetupHoverAnimations(exitItem);
    }

    private void SetupHoverAnimations(MenuFlyoutItem item)
    {
        item.PointerEntered += (s, e) =>
        {
            if (s is MenuFlyoutItem menuItem)
            {
                var visual = ElementCompositionPreview.GetElementVisual(menuItem);
                var compositor = visual.Compositor;
                var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
                scaleAnim.InsertKeyFrame(0f, new Vector3(1f, 1f, 1f));
                scaleAnim.InsertKeyFrame(1f, new Vector3(1.02f, 1.02f, 1f));
                scaleAnim.Duration = TimeSpan.FromMilliseconds(MenuItemHoverAnimationMs);
                visual.StartAnimation("Scale", scaleAnim);
            }
        };
        item.PointerExited += (s, e) =>
        {
            if (s is MenuFlyoutItem menuItem)
            {
                var visual = ElementCompositionPreview.GetElementVisual(menuItem);
                var compositor = visual.Compositor;
                var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
                scaleAnim.InsertKeyFrame(0f, new Vector3(1.02f, 1.02f, 1f));
                scaleAnim.InsertKeyFrame(1f, new Vector3(1f, 1f, 1f));
                scaleAnim.Duration = TimeSpan.FromMilliseconds(MenuItemHoverAnimationMs);
                visual.StartAnimation("Scale", scaleAnim);
            }
        };
    }

    private void ApplyOpenAnimation()
    {
        if (_contextMenu?.Target is not MenuFlyoutPresenter presenter) return;
        var visual = ElementCompositionPreview.GetElementVisual(presenter);
        var compositor = visual.Compositor;

        var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
        scaleAnim.InsertKeyFrame(0f, new Vector3(0.9f, 0.9f, 1f));
        scaleAnim.InsertKeyFrame(1f, new Vector3(1f, 1f, 1f));
        scaleAnim.Duration = TimeSpan.FromMilliseconds(MenuOpenCloseAnimationMs);

        var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
        opacityAnim.InsertKeyFrame(0f, 0f);
        opacityAnim.InsertKeyFrame(1f, 1f);
        opacityAnim.Duration = TimeSpan.FromMilliseconds(MenuOpenCloseAnimationMs);

        visual.StartAnimation("Scale", scaleAnim);
        visual.StartAnimation("Opacity", opacityAnim);
    }

    private void ApplyCloseAnimation()
    {
        if (_contextMenu?.Target is not MenuFlyoutPresenter presenter) return;
        var visual = ElementCompositionPreview.GetElementVisual(presenter);
        var compositor = visual.Compositor;

        var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
        scaleAnim.InsertKeyFrame(0f, new Vector3(1f, 1f, 1f));
        scaleAnim.InsertKeyFrame(1f, new Vector3(0.95f, 0.95f, 1f));
        scaleAnim.Duration = TimeSpan.FromMilliseconds(MenuOpenCloseAnimationMs);

        var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
        opacityAnim.InsertKeyFrame(0f, 1f);
        opacityAnim.InsertKeyFrame(1f, 0f);
        opacityAnim.Duration = TimeSpan.FromMilliseconds(MenuOpenCloseAnimationMs);

        visual.StartAnimation("Scale", scaleAnim);
        visual.StartAnimation("Opacity", opacityAnim);
    }

    private void AttachRightClickHandler()
    {
        RootGrid.PointerPressed += OnPointerPressed;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            var point = e.GetCurrentPoint(RootGrid);
            if (point.Properties.IsRightButtonPressed && _contextMenu != null)
            {
                e.Handled = true;
                var position = point.Position;
                _contextMenu.ShowAt(RootGrid, new Windows.Foundation.Point(position.X, position.Y));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Menu show error: {ex}");
        }
    }

    private void ReloadWallpaper()
    {
        int screenWidth = GetSystemMetrics(SM_CXSCREEN);
        int screenHeight = GetSystemMetrics(SM_CYSCREEN);
        SetWallpaperBackground(screenWidth, screenHeight);
    }

    private void SetWallpaperBackground(int screenWidth, int screenHeight)
    {
        try
        {
            var wallpaperPath = new System.Text.StringBuilder(MAX_PATH);
            if (SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)MAX_PATH, wallpaperPath, 0))
            {
                string path = wallpaperPath.ToString();
                if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                {
                    var bitmap = new BitmapImage();
                    bitmap.UriSource = new System.Uri(path);
                    var image = new Image
                    {
                        Source = bitmap,
                        Stretch = Stretch.UniformToFill,
                        Margin = new Thickness(0),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Stretch
                    };

                    RootGrid.Children.Clear();
                    RootGrid.Children.Add(image);
                }
            }
        }
        catch
        {
            // Fallback: leave transparent if wallpaper can't be loaded
        }
    }
}
