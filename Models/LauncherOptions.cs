namespace WinUI3Desktop;

public enum AppThemeKind
{
    System,
    Light,
    Dark
}

public enum SurfaceMaterialKind
{
    GaussianBlur,
    Acrylic,
    Mica,
    LiquidGlass
}

/// <summary>底部上滑呼出手势：Auto 按设备有无触屏决定。</summary>
public enum SwipeGestureMode
{
    Auto,
    Enabled,
    Disabled
}

/// <summary>全局配置模型；持久化为 %LOCALAPPDATA%\MyDock\launcher-settings.json。</summary>
public sealed class LauncherOptions
{
    public AppThemeKind Theme { get; set; }
    public SurfaceMaterialKind Material { get; set; } = SurfaceMaterialKind.GaussianBlur;
    public double MaterialOpacity { get; set; } = 0.78;
    public double TintStrength { get; set; } = 0.68;
    public double CornerRadius { get; set; } = 22;
    public double TileSize { get; set; } = 100;
    public bool ShowLauncherButton { get; set; } = true;
    public SwipeGestureMode BottomSwipe { get; set; } = SwipeGestureMode.Auto;

    /// <summary>接管桌面：隐藏原生任务栏，退出/崩溃时恢复。</summary>
    public bool TakeOverDesktop { get; set; } = true;

    public List<ApplicationItem> PinnedApplications { get; set; } = new();
    public List<ApplicationItem> CustomApplications { get; set; } = new();

    public static LauncherOptions CreateDefault()
    {
        return new LauncherOptions
        {
            PinnedApplications = new List<ApplicationItem>
            {
                new() { Name = "资源管理器", LaunchPath = "explorer.exe" },
                new() { Name = "记事本", LaunchPath = "notepad.exe" },
                new() { Name = "设置", LaunchPath = "ms-settings:" }
            }
        };
    }
}
