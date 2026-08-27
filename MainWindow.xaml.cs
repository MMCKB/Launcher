using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using WinRT.Interop;
using Windows.ApplicationModel.DataTransfer;
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
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpFrameChanged = 0x0020;
    private const int MaxPath = 260;

    private static readonly IntPtr HwndBottom = new(1);
    private readonly List<DockApplication> _allApplications = new();
    private DockApplication? _draggedApplication;
    private SettingsWindow? _settingsWindow;
    private int _drawerPage;

    public MainWindow()
    {
        InitializeComponent();
        ConfigureDesktopWindow();
        ReloadWallpaper();

        LauncherSettings.Changed += OnSettingsChanged;
        RefreshApplications();
        ApplyDockConfiguration();
    }

    private void ConfigureDesktopWindow()
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
        SetWindowPos(hWnd, HwndBottom, 0, 0, screenWidth, screenHeight,
            SwpShowWindow | SwpFrameChanged | SwpNoActivate);

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        appWindow.Title = "MyDock Desktop";
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            RefreshApplications();
            ApplyDockConfiguration();
        });
    }

    private void RefreshApplications()
    {
        _allApplications.Clear();
        _allApplications.AddRange(ApplicationCatalog.GetSystemApplications());

        foreach (var custom in LauncherSettings.Current.CustomApplications)
        {
            AddApplicationIfMissing(_allApplications, custom);
        }

        foreach (var dockApp in LauncherSettings.Current.DockApplications)
        {
            AddApplicationIfMissing(_allApplications, dockApp);
        }

        _drawerPage = 0;
        RenderDockApplications();
        RenderDrawerPage();
    }

    private static void AddApplicationIfMissing(List<DockApplication> destination, DockApplication application)
    {
        if (destination.Any(item => string.Equals(item.LaunchPath, application.LaunchPath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        destination.Add(application);
    }

    private static IEnumerable<DockApplication> GetSystemApplications()
    {
        var results = new List<DockApplication>
        {
            new() { Name = "资源管理器", LaunchPath = "explorer.exe" },
            new() { Name = "记事本", LaunchPath = "notepad.exe" },
            new() { Name = "设置", LaunchPath = "ms-settings:" }
        };

        var startMenuFolders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        };

        foreach (var folder in startMenuFolders.Where(Directory.Exists))
        {
            try
            {
                foreach (var shortcut in Directory.EnumerateFiles(folder, "*.lnk", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileNameWithoutExtension(shortcut);
                    if (string.IsNullOrWhiteSpace(name) || name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    AddApplicationIfMissing(results, new DockApplication
                    {
                        Name = name,
                        LaunchPath = shortcut
                    });
                }
            }
            catch
            {
                // Some protected Start Menu folders are intentionally skipped.
            }
        }

        return results.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).Take(180);
    }

    private void RenderDockApplications()
    {
        DockAppsPanel.Children.Clear();

        foreach (var application in LauncherSettings.Current.DockApplications)
        {
            var button = new Button
            {
                Tag = application,
                Style = (Style)RootGrid.Resources["DockButtonStyle"]
            };
            ToolTipService.SetToolTip(button, application.Name);

            button.Click += DockApplication_Click;
            button.RightTapped += DockApplication_RightTapped;
            button.Content = CreateDockTile(application);
            DockAppsPanel.Children.Add(button);
        }
    }

    private static UIElement CreateDockTile(DockApplication application)
    {
        var root = new Grid { Width = 52, Height = 60 };
        var tile = new Border
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(GetApplicationColor(application.Name)),
            Child = new TextBlock
            {
                Text = application.Initial,
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        root.Children.Add(tile);
        root.Children.Add(new Ellipse
        {
            Width = 5,
            Height = 5,
            Fill = new SolidColorBrush(Color.FromArgb(255, 158, 199, 255)),
            VerticalAlignment = VerticalAlignment.Bottom
        });
        return root;
    }

    private static Color GetApplicationColor(string name)
    {
        var palette = new[]
        {
            Color.FromArgb(255, 54, 111, 221),
            Color.FromArgb(255, 118, 78, 207),
            Color.FromArgb(255, 38, 151, 118),
            Color.FromArgb(255, 223, 119, 45),
            Color.FromArgb(255, 207, 71, 102)
        };
        return palette[(int)((uint)name.GetHashCode() % palette.Length)];
    }

    private void DockApplication_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DockApplication application })
        {
            LaunchApplication(application);
        }
    }

    private void DockApplication_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not Button { Tag: DockApplication application } || LauncherSettings.Current.IsLocked)
        {
            return;
        }

        e.Handled = true;
        var menu = new MenuFlyout();
        var remove = new MenuFlyoutItem
        {
            Text = "从 Dock 移除",
            Icon = new FontIcon { Glyph = "\uE71B" }
        };
        remove.Click += (_, _) =>
        {
            LauncherSettings.Current.DockApplications.RemoveAll(item => item.Id == application.Id);
            LauncherSettings.Save();
        };
        menu.Items.Add(remove);
        menu.ShowAt((FrameworkElement)sender, new FlyoutShowOptions { Position = e.GetPosition((UIElement)sender) });
    }

    private void OpenDrawerButton_Click(object sender, RoutedEventArgs e)
    {
        if (DrawerHost.Visibility == Visibility.Visible)
        {
            HideDrawer();
        }
        else
        {
            ShowDrawer();
        }
    }

    private void ShowDrawer()
    {
        var configuration = LauncherSettings.Current;
        DrawerHost.Margin = new Thickness(0, 0, 0, configuration.SeparateDrawer ? 104 + configuration.DrawerGap : 100);
        DrawerSurface.CornerRadius = configuration.SeparateDrawer
            ? new CornerRadius(24)
            : new CornerRadius(24, 24, 8, 8);
        DockSurface.CornerRadius = configuration.SeparateDrawer
            ? new CornerRadius(configuration.CornerRadius)
            : new CornerRadius(8, 8, configuration.CornerRadius, configuration.CornerRadius);

        DrawerHost.Visibility = Visibility.Visible;
        AnimateDrawer(true, configuration.SeparateDrawer);
    }

    private void HideDrawer()
    {
        AnimateDrawer(false, LauncherSettings.Current.SeparateDrawer);
        DrawerHost.Visibility = Visibility.Collapsed;
        DockSurface.CornerRadius = new CornerRadius(LauncherSettings.Current.CornerRadius);
    }

    private void AnimateDrawer(bool isOpening, bool separate)
    {
        var visual = ElementCompositionPreview.GetElementVisual(DrawerSurface);
        var compositor = visual.Compositor;
        var duration = TimeSpan.FromMilliseconds(isOpening ? 180 : 120);
        var startOffset = separate ? 16f : 56f;

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0f, isOpening ? 0f : 1f);
        opacity.InsertKeyFrame(1f, isOpening ? 1f : 0f);
        opacity.Duration = duration;

        var offset = compositor.CreateVector3KeyFrameAnimation();
        offset.InsertKeyFrame(0f, new Vector3(0, isOpening ? startOffset : 0, 0));
        offset.InsertKeyFrame(1f, new Vector3(0, isOpening ? 0 : startOffset, 0));
        offset.Duration = duration;

        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Offset", offset);
    }

    private void DrawerSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _drawerPage = 0;
        RenderDrawerPage();
    }

    private IEnumerable<DockApplication> FilteredApplications()
    {
        var activeSearchBox = LauncherSettings.Current.SearchAtBottom ? DrawerSearchBoxBottom : DrawerSearchBox;
        var query = activeSearchBox.Text?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(query)
            ? _allApplications
            : _allApplications.Where(item => item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private void RenderDrawerPage()
    {
        var pageSize = Math.Max(1, LauncherSettings.Current.DrawerRows * LauncherSettings.Current.DrawerColumns);
        var filtered = FilteredApplications().ToList();
        var pageCount = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)pageSize));
        _drawerPage = Math.Clamp(_drawerPage, 0, pageCount - 1);

        DrawerRepeater.ItemsSource = filtered.Skip(_drawerPage * pageSize).Take(pageSize).ToList();
        DrawerPageText.Text = $"{_drawerPage + 1} / {pageCount}";
        DrawerGridLayout.MinItemWidth = 92;
        DrawerGridLayout.MinItemHeight = 100;
    }

    private void PreviousDrawerPage_Click(object sender, RoutedEventArgs e)
    {
        _drawerPage = Math.Max(0, _drawerPage - 1);
        RenderDrawerPage();
    }

    private void NextDrawerPage_Click(object sender, RoutedEventArgs e)
    {
        _drawerPage++;
        RenderDrawerPage();
    }

    private void DrawerApplication_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DockApplication application })
        {
            LaunchApplication(application);
        }
    }

    private void DrawerApplication_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not Button { Tag: DockApplication application })
        {
            return;
        }

        e.Handled = true;
        var menu = new MenuFlyout();
        var add = new MenuFlyoutItem
        {
            Text = LauncherSettings.Current.IsLocked ? "Dock 已锁定" : "添加到 Dock",
            Icon = new FontIcon { Glyph = "\uE710" },
            IsEnabled = !LauncherSettings.Current.IsLocked
        };
        add.Click += (_, _) => AddApplicationToDock(application);
        menu.Items.Add(add);
        menu.Items.Add(new MenuFlyoutSeparator());
        var open = new MenuFlyoutItem { Text = "打开", Icon = new FontIcon { Glyph = "\uE8A7" } };
        open.Click += (_, _) => LaunchApplication(application);
        menu.Items.Add(open);
        menu.ShowAt((FrameworkElement)sender, new FlyoutShowOptions { Position = e.GetPosition((UIElement)sender) });
    }

    private void DrawerApplication_DragStarting(object sender, DragStartingEventArgs args)
    {
        _draggedApplication = sender is FrameworkElement { Tag: DockApplication application } ? application : null;
        args.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private void DockAppsPanel_DragOver(object sender, DragEventArgs e)
    {
        if (!LauncherSettings.Current.IsLocked && _draggedApplication is not null)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private void DockAppsPanel_Drop(object sender, DragEventArgs e)
    {
        if (_draggedApplication is not null)
        {
            AddApplicationToDock(_draggedApplication);
        }

        _draggedApplication = null;
    }

    private static void AddApplicationToDock(DockApplication application)
    {
        if (LauncherSettings.Current.IsLocked || LauncherSettings.Current.DockApplications.Any(item =>
                string.Equals(item.LaunchPath, application.LaunchPath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        LauncherSettings.Current.DockApplications.Add(application);
        LauncherSettings.Save();
    }

    private static void LaunchApplication(DockApplication application)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = application.LaunchPath,
                UseShellExecute = true
            });
        }
        catch
        {
            // An unavailable shortcut or executable must not break the desktop.
        }
    }

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsInteractiveLauncherElement(e.OriginalSource as DependencyObject))
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
        var settings = new MenuFlyoutItem { Text = "设置", Icon = new FontIcon { Glyph = "\uE713" } };
        settings.Click += (_, _) => OpenSettingsWindow();
        menu.Items.Add(settings);
        var refresh = new MenuFlyoutItem { Text = "刷新壁纸", Icon = new FontIcon { Glyph = "\uE72C" } };
        refresh.Click += (_, _) => ReloadWallpaper();
        menu.Items.Add(refresh);
        menu.Items.Add(new MenuFlyoutSeparator());
        var exit = new MenuFlyoutItem { Text = "退出", Icon = new FontIcon { Glyph = "\uE7E8" } };
        exit.Click += (_, _) => Application.Current.Exit();
        menu.Items.Add(exit);
        menu.ShowAt(RootGrid, new FlyoutShowOptions { Position = point.Position });
    }

    private bool IsInteractiveLauncherElement(DependencyObject? element)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, DockHost) || ReferenceEquals(element, DrawerHost))
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private void OpenSettingsWindow()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.Activate();
    }

    private void ApplyDockConfiguration()
    {
        var configuration = LauncherSettings.Current;
        DockSurface.CornerRadius = new CornerRadius(configuration.CornerRadius);
        DrawerSurface.CornerRadius = new CornerRadius(configuration.CornerRadius);
        DrawerSurface.Height = Math.Clamp(166 + (configuration.DrawerRows * 110), 330, 740);
        DockSurface.Background = CreateMaterialBrush(configuration);
        DrawerSurface.Background = CreateMaterialBrush(configuration);
        DrawerSearchBox.Visibility = configuration.SearchAtBottom ? Visibility.Collapsed : Visibility.Visible;
        DrawerSearchBoxBottom.Visibility = configuration.SearchAtBottom ? Visibility.Visible : Visibility.Collapsed;
        RenderDrawerPage();
    }

    private static Brush CreateMaterialBrush(DockConfiguration configuration)
    {
        var opacity = (byte)Math.Clamp((int)Math.Round(configuration.Opacity * 255), 80, 250);
        var blurInfluence = Math.Clamp(configuration.BlurAmount / 100d, 0, 1);
        var mixedTint = Math.Clamp(configuration.TintStrength * (0.72 + (blurInfluence * 0.22)), 0.1, 0.95);
        return configuration.Material switch
        {
            DockMaterialKind.Mica => new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops = new GradientStopCollection
                {
                    new GradientStop { Color = Color.FromArgb(opacity, 43, 57, 88), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(opacity, 24, 34, 57), Offset = 1 }
                }
            },
            DockMaterialKind.LiquidGlass => new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops = new GradientStopCollection
                {
                    new GradientStop { Color = Color.FromArgb((byte)Math.Min(250, opacity + 20), 196, 219, 255), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(opacity, 62, 91, 160), Offset = 0.42 },
                    new GradientStop { Color = Color.FromArgb(opacity, 30, 40, 75), Offset = 1 }
                }
            },
            DockMaterialKind.Acrylic => new AcrylicBrush
            {
                TintColor = Color.FromArgb(255, 34, 52, 90),
                TintOpacity = mixedTint,
                FallbackColor = Color.FromArgb(opacity, 34, 52, 90)
            },
            _ => new AcrylicBrush
            {
                TintColor = Color.FromArgb(255, 23, 35, 60),
                TintOpacity = Math.Clamp(mixedTint * 0.78, 0.1, 0.92),
                FallbackColor = Color.FromArgb(opacity, 23, 35, 60)
            }
        };
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

            WallpaperImage.Source = new BitmapImage { UriSource = new Uri(path) };
        }
        catch
        {
            WallpaperImage.Source = null;
        }
    }
}
