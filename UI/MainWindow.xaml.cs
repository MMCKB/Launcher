using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using WinRT.Interop;

namespace WinUI3Desktop;

/// <summary>
/// 桌面外壳主窗口：全屏底窗 + 壁纸 + 状态时钟 + 启动台。
/// 窗口管理、材质、时钟、热键在这里；启动台交互在 MainWindow.Launchpad.cs；
/// 控制中心在 MainWindow.ControlCenter.cs。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly List<ApplicationItem> _allApplications = new();
    private SettingsWindow? _settingsWindow;
    private string? _runningSignature;
    private string? _materialSignature;
    private string? _tileSignature;
    private readonly DispatcherTimer _runningAppsTimer;
    private readonly DispatcherTimer _clockTimer;

    // 全局热键（Ctrl+Alt+L）：子类化 WNDPROC 接收 WM_HOTKEY
    private const int HotKeyId = 0xA11B;
    private bool _hotKeyInstalled;
    private IntPtr _originalWndProc;
    private Win32.WndProc? _wndProcDelegate;

    public MainWindow()
    {
        InitializeComponent();
        ConfigureDesktopWindow();
        ReloadWallpaper();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();
        UpdateClock();

        // 定时刷新正在运行的应用（每2秒），顺带看门狗：确保原生任务栏保持隐藏
        _runningAppsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _runningAppsTimer.Tick += (_, _) =>
        {
            if (LauncherSettings.Current.TakeOverDesktop)
            {
                EnsureNativeTaskbarHidden();
            }

            RefreshRunningApplications();
        };
        _runningAppsTimer.Start();

        Closed += (_, _) =>
        {
            RemoveHotKey();
            LauncherSettings.Changed -= OnSettingsChanged;
            LauncherSettings.PreviewChanged -= OnPreviewChanged;
            RestoreNativeTaskbar();
        };

        LauncherSettings.Changed += OnSettingsChanged;
        LauncherSettings.PreviewChanged += OnPreviewChanged;
        InstallHotKey();

        // 启动台网格使用代码工厂生成瓦片，尺寸随设置变化
        AppsRepeater.ItemTemplate = new AppTileElementFactory(this);
        PinnedRepeater.ItemTemplate = new AppTileElementFactory(this);

        RefreshApplications();
        ApplyOptions();
        RefreshRunningApplications();

        // 诊断开关：LAUNCHER_DEBUG_OPEN=1 时启动即展开启动台
        if (Environment.GetEnvironmentVariable("LAUNCHER_DEBUG_OPEN") == "1")
        {
            DispatcherQueue.TryEnqueue(() => ShowLaunchpad(LaunchSource.HotKey));
        }
    }

    /// <summary>后台预提取全部应用图标，启动台首次渲染时缓存已就绪，不阻塞 UI 线程。</summary>
    private void PreloadIcons()
    {
        var snapshot = _allApplications.ToList();
        Task.Run(() =>
        {
            foreach (var application in snapshot)
            {
                IconCache.GetCachedIconPath(application);
            }
        });
    }

    private void RefreshRunningApplications()
    {
        // UI 线程只做快照；进程枚举较慢，放到线程池执行，避免每 2 秒卡顿
        var knownSnapshot = _allApplications.ToList();
        Task.Run(() =>
        {
            var runningApps = ApplicationCatalog.GetRunningApplications(knownSnapshot);
            DispatcherQueue.TryEnqueue(() =>
            {
                // 运行集合没变化就完全不重绘
                var signature = string.Join("|", runningApps.Select(item => item.LaunchPath));
                if (signature == _runningSignature)
                {
                    return;
                }

                _runningSignature = signature;
                foreach (var application in _allApplications)
                {
                    application.IsRunning = false;
                }

                foreach (var application in runningApps)
                {
                    application.IsRunning = true;
                }

                RenderRunningSection(runningApps);
                RenderLaunchpad();
            });
        });
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            RefreshApplications();
            ApplyOptions();
            _runningSignature = null;
            RefreshRunningApplications();
            PreloadIcons();
        });
    }

    /// <summary>滑块拖动中的预览：只套用外观，不重新枚举应用。</summary>
    private void OnPreviewChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(ApplyOptions);
    }

    private void RefreshApplications()
    {
        // 应用目录枚举（PackageManager + AppsFolder）较慢，放线程池执行，完成后回 UI 线程套用
        Task.Run(() =>
        {
            var systemApps = ApplicationCatalog.GetSystemApplications();
            DispatcherQueue.TryEnqueue(() =>
            {
                _allApplications.Clear();
                _allApplications.AddRange(systemApps);

                foreach (var custom in LauncherSettings.Current.CustomApplications)
                {
                    ApplicationCatalog.AddIfMissing(_allApplications, custom);
                }

                foreach (var pinned in LauncherSettings.Current.PinnedApplications)
                {
                    ApplicationCatalog.AddIfMissing(_allApplications, pinned);
                }

                RenderLaunchpad();
                PreloadIcons();
            });
        });
    }

    /// <summary>应用整套设置（主题/材质/瓦片/手势/任务栏接管）；滑块预览会高频调用，材质与网格用签名去重。</summary>
    private void ApplyOptions()
    {
        var options = LauncherSettings.Current;

        RootGrid.RequestedTheme = options.Theme switch
        {
            AppThemeKind.Light => ElementTheme.Light,
            AppThemeKind.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        var isLight = IsEffectiveLightTheme(options);

        // 材质相关：仅在参数变化时重建效果链与着色层（透明度/着色强度/主题实时生效）
        var materialSignature = $"{options.Theme}|{options.Material}|{options.MaterialOpacity:0.00}|{options.TintStrength:0.00}";
        if (materialSignature != _materialSignature)
        {
            _materialSignature = materialSignature;
            MaterialEffects.Apply(LaunchpadMaterialHost, options.Material, options, isLight);
            MaterialEffects.ApplyChip(LauncherButton, options, isLight);
            MaterialEffects.ApplyChip(ClockChip, options, isLight);
            MaterialEffects.ApplyPanel(ControlCenterSurface, options, isLight);
        }

        var stroke = isLight
            ? Windows.UI.Color.FromArgb(0x2E, 0, 0, 0)
            : Windows.UI.Color.FromArgb(0x40, 255, 255, 255);
        LauncherButton.BorderBrush = new SolidColorBrush(stroke);
        ClockChip.BorderBrush = new SolidColorBrush(stroke);

        var foreground = isLight
            ? Windows.UI.Color.FromArgb(0xE6, 0x1F, 0x2A, 0x3A)
            : Microsoft.UI.Colors.White;
        LauncherButton.Foreground = new SolidColorBrush(foreground);
        ClockTime.Foreground = new SolidColorBrush(foreground);
        ClockDate.Foreground = new SolidColorBrush(foreground);

        LauncherButton.Visibility = options.ShowLauncherButton ? Visibility.Visible : Visibility.Collapsed;

        var tileSize = GetTileSize();
        var tileSignature = $"{tileSize:0}";
        if (tileSignature != _tileSignature)
        {
            _tileSignature = tileSignature;
            AppsGridLayout.MinItemWidth = tileSize;
            AppsGridLayout.MinItemHeight = tileSize + 42;
            PinnedGridLayout.MinItemWidth = tileSize;
            PinnedGridLayout.MinItemHeight = tileSize + 42;
            RenderLaunchpad();
        }

        RefreshGestureState();
        ApplyTakeOverDesktop();
    }

    internal static bool IsEffectiveLightTheme(LauncherOptions options)
    {
        return options.Theme switch
        {
            AppThemeKind.Light => true,
            AppThemeKind.Dark => false,
            _ => Application.Current.RequestedTheme == ApplicationTheme.Light
        };
    }

    private static double GetTileSize() => Math.Clamp(LauncherSettings.Current.TileSize, 84, 128);

    /// <summary>瓦片悬停放大：以中心为锚点缩放 1.12（启动台里没有“从底部立起”的语境）。</summary>
    private static void AttachHoverMagnification(UIElement element, double width, double height)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint = new Vector3((float)(width / 2), (float)(height / 2), 0);

        element.PointerEntered += (_, _) => AnimateTileScale(visual, 1.12f);
        element.PointerExited += (_, _) => AnimateTileScale(visual, 1f);
    }

    private static void AnimateTileScale(Visual visual, float scale)
    {
        var compositor = visual.Compositor;
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0f, visual.Scale);
        animation.InsertKeyFrame(1f, new Vector3(scale, scale, 1f),
            compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0.3f, 1f)));
        animation.Duration = TimeSpan.FromMilliseconds(160);
        visual.StartAnimation("Scale", animation);
    }

    private static Image? TryLoadApplicationIcon(ApplicationItem item, double iconSize)
    {
        try
        {
            var iconPath = IconCache.GetCachedIconPath(item);
            if (iconPath is null)
            {
                return null;
            }

            return new Image
            {
                Source = new BitmapImage(new Uri(iconPath)),
                Width = iconSize,
                Height = iconSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Stretch = Stretch.Uniform
            };
        }
        catch
        {
            return null;
        }
    }

    private static void LaunchApplication(ApplicationItem item)
    {
        try
        {
            if (item.UseAppsFolderActivation)
            {
                ApplicationCatalog.ActivateAppsFolderApplication(item.LaunchPath);
                return;
            }

            // UseShellExecute 让 ShellExecute 处理 .lnk、ms-settings: 等所有外壳目标
            Process.Start(new ProcessStartInfo
            {
                FileName = item.LaunchPath,
                UseShellExecute = true
            });
        }
        catch
        {
            // An unavailable shortcut or executable must not break the desktop.
        }
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockTime.Text = now.ToString("HH:mm");
        ClockDate.Text = now.ToString("M月d日 ddd");
    }

    // ---- 窗口形态与任务栏 ----

    private void ConfigureDesktopWindow()
    {
        var hWnd = WindowNative.GetWindowHandle(this);

        var style = Win32.GetWindowLong(hWnd, Win32.GwlStyle);
        style &= ~Win32.WsBorder;
        style &= ~Win32.WsDlgFrame;
        style &= ~Win32.WsThickFrame;
        Win32.SetWindowLong(hWnd, Win32.GwlStyle, style);

        var exStyle = Win32.GetWindowLong(hWnd, Win32.GwlExStyle);
        exStyle &= ~Win32.WsExClientEdge;
        exStyle &= ~Win32.WsExWindowEdge;
        exStyle &= ~Win32.WsExStaticEdge;
        Win32.SetWindowLong(hWnd, Win32.GwlExStyle, exStyle);

        // Disable DWM non-client rendering to remove white borders
        int ncRenderingPolicy = Win32.DwmNcrpDisabled;
        Win32.DwmSetWindowAttribute(hWnd, Win32.DwmwaNcRenderingPolicy, ref ncRenderingPolicy, sizeof(int));

        var margins = new Win32.Margins { LeftWidth = -1, RightWidth = -1, TopHeight = -1, BottomHeight = -1 };
        Win32.DwmExtendFrameIntoClientArea(hWnd, ref margins);

        // 全屏窗口不要圆角（四角会露出下层内容），并去掉 Win11 窗口边框（四周白框）
        var cornerPreference = Win32.DwmwcpDoNotRound;
        Win32.DwmSetWindowAttribute(hWnd, Win32.DwmwaCornerPreference, ref cornerPreference, sizeof(int));

        var borderColor = Win32.DwmwaColorNone;
        Win32.DwmSetWindowAttribute(hWnd, Win32.DwmwaBorderColor, ref borderColor, sizeof(int));

        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        var screenWidth = Win32.GetSystemMetrics(Win32.SmCxScreen);
        var screenHeight = Win32.GetSystemMetrics(Win32.SmCyScreen);

        appWindow.MoveAndResize(new Windows.Graphics.RectInt32(0, 0, screenWidth, screenHeight));
        Win32.SetWindowPos(hWnd, Win32.HwndBottom, 0, 0, screenWidth, screenHeight,
            Win32.SwpShowWindow | Win32.SwpFrameChanged | Win32.SwpNoActivate);

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        appWindow.Title = "Launcher";

        // 启动器是桌面替代品，隐藏原生任务栏（含多显示器副任务栏），退出时恢复
        HideNativeTaskbar();
    }

    private void ApplyTakeOverDesktop()
    {
        if (LauncherSettings.Current.TakeOverDesktop)
        {
            HideNativeTaskbar();
        }
        else
        {
            RestoreNativeTaskbar();
        }
    }

    private void HideNativeTaskbar()
    {
        var tray = Win32.FindWindow("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero && Win32.IsWindowVisible(tray))
        {
            Win32.ShowWindow(tray, Win32.SwHide);
        }

        var secondary = Win32.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_SecondaryTrayWnd", null);
        while (secondary != IntPtr.Zero)
        {
            if (Win32.IsWindowVisible(secondary))
            {
                Win32.ShowWindow(secondary, Win32.SwHide);
            }

            secondary = Win32.FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null);
        }
    }

    private void EnsureNativeTaskbarHidden()
    {
        // explorer 重启后任务栏会重新出现，定时器里顺手再压一次
        if (Win32.IsWindowVisible(Win32.FindWindow("Shell_TrayWnd", null)))
        {
            HideNativeTaskbar();
        }
    }

    public static void RestoreNativeTaskbar()
    {
        var tray = Win32.FindWindow("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero)
        {
            Win32.ShowWindow(tray, Win32.SwShow);
        }

        var secondary = Win32.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_SecondaryTrayWnd", null);
        while (secondary != IntPtr.Zero)
        {
            Win32.ShowWindow(secondary, Win32.SwShow);
            secondary = Win32.FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null);
        }
    }

    // ---- 全局热键 Ctrl+Alt+L ----

    private void InstallHotKey()
    {
        try
        {
            var hWnd = WindowNative.GetWindowHandle(this);
            _wndProcDelegate = HookWndProc; // 字段持有，防止委托被 GC 回收
            _originalWndProc = Win32.GetWindowLongPtr(hWnd, Win32.GwlpWndProc);
            Win32.SetWindowLongPtr(hWnd, Win32.GwlpWndProc, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
            _hotKeyInstalled = Win32.RegisterHotKey(hWnd, HotKeyId, Win32.ModControl | Win32.ModAlt, (uint)'L');
        }
        catch
        {
            // 子类化失败只损失热键，不影响其他交互。
            _hotKeyInstalled = false;
        }
    }

    private void RemoveHotKey()
    {
        try
        {
            var hWnd = WindowNative.GetWindowHandle(this);
            if (_hotKeyInstalled)
            {
                Win32.UnregisterHotKey(hWnd, HotKeyId);
            }

            if (_originalWndProc != IntPtr.Zero)
            {
                Win32.SetWindowLongPtr(hWnd, Win32.GwlpWndProc, _originalWndProc);
            }
        }
        catch
        {
            // 窗口销毁阶段的原生恢复失败可以忽略。
        }

        _originalWndProc = IntPtr.Zero;
        _wndProcDelegate = null;
        _hotKeyInstalled = false;
    }

    private IntPtr HookWndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == Win32.WmHotKey && (int)wParam == HotKeyId)
        {
            DispatcherQueue.TryEnqueue(() => ToggleLaunchpad());
            return IntPtr.Zero;
        }

        return Win32.CallWindowProc(_originalWndProc, hWnd, message, wParam, lParam);
    }

    // ---- 桌面右键菜单与快捷键 ----

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_launchpadOpen || IsInteractiveLauncherElement(e.OriginalSource as DependencyObject))
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
        var launchpad = new MenuFlyoutItem { Text = "打开启动台", Icon = new FontIcon { Glyph = "\uE71D" } };
        launchpad.Click += (_, _) => ShowLaunchpad(LaunchSource.Menu);
        menu.Items.Add(launchpad);
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
            if (ReferenceEquals(element, LaunchpadRoot) || ReferenceEquals(element, ClockChip) ||
                ReferenceEquals(element, LauncherButton) || ReferenceEquals(element, BottomEdgeZone))
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

    private void CloseAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_controlCenterOpen)
        {
            args.Handled = true;
            CloseControlCenter();
            return;
        }

        if (!_launchpadOpen)
        {
            return;
        }

        args.Handled = true;
        HideLaunchpad();
    }

    private void ToggleAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleLaunchpad();
    }

    // ---- 壁纸 ----

    private void ReloadWallpaper()
    {
        try
        {
            var wallpaperPath = new System.Text.StringBuilder(Win32.MaxPath);
            if (!Win32.SystemParametersInfo(Win32.SpiGetDeskWallpaper, (uint)Win32.MaxPath, wallpaperPath, 0))
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
