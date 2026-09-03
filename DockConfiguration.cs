namespace WinUI3Desktop;

public enum DockMaterialKind
{
    GaussianBlur,
    Acrylic,
    Mica,
    LiquidGlass
}

public sealed class DockApplication
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新应用";
    public string LaunchPath { get; set; } = string.Empty;
    public string? IconPath { get; set; }
    public bool UseAppsFolderActivation { get; set; }
    public bool IsCustom { get; set; }
    public bool IsRunning { get; set; }

    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();
}

public sealed class DockConfiguration
{
    public bool IsLocked { get; set; }
    public DockMaterialKind Material { get; set; } = DockMaterialKind.GaussianBlur;
    public double BlurAmount { get; set; } = 28;
    public double Opacity { get; set; } = 0.78;
    public double TintStrength { get; set; } = 0.68;
    public double CornerRadius { get; set; } = 26;
    public bool SeparateDrawer { get; set; }
    public double DrawerGap { get; set; } = 12;
    public bool SearchAtBottom { get; set; }
    public int DrawerRows { get; set; } = 3;
    public int DrawerColumns { get; set; } = 6;
    public List<DockApplication> DockApplications { get; set; } = new();
    public List<DockApplication> CustomApplications { get; set; } = new();

    public static DockConfiguration CreateDefault()
    {
        return new DockConfiguration
        {
            DockApplications = new List<DockApplication>
            {
                new() { Name = "资源管理器", LaunchPath = "explorer.exe" },
                new() { Name = "记事本", LaunchPath = "notepad.exe" },
                new() { Name = "设置", LaunchPath = "ms-settings:" }
            }
        };
    }
}
