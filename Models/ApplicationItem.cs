using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WinUI3Desktop;

/// <summary>启动台里的一个可启动条目：Win32 快捷方式、URI 或 shell:AppsFolder 的 AUMID。</summary>
public sealed class ApplicationItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新应用";
    public string LaunchPath { get; set; } = string.Empty;
    public string? IconPath { get; set; }
    public bool UseAppsFolderActivation { get; set; }
    public bool IsCustom { get; set; }
    public bool IsRunning { get; set; }

    /// <summary>运行中条目的主窗口句柄（仅运行列表填充，不落盘），用于点击切换窗口。</summary>
    [JsonIgnore]
    public IntPtr WindowHandle { get; set; }

    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();

    private ImageSource? _tileIcon;
    private bool _tileIconResolved;

    /// <summary>瓦片图标（从 IconCache 提取），null 时用首字母兜底。</summary>
    [JsonIgnore]
    public ImageSource? TileIcon
    {
        get
        {
            if (!_tileIconResolved)
            {
                _tileIconResolved = true;
                _tileIcon = IconCache.GetTileIcon(this);
            }

            return _tileIcon;
        }
    }

    [JsonIgnore]
    public Visibility InitialVisibility => TileIcon is null ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public Brush? TileBackground => TileIcon is null ? new SolidColorBrush(AccentColor) : null;

    public Windows.UI.Color AccentColor => GetAccentColor(Name);

    public static Windows.UI.Color GetAccentColor(string name)
    {
        var palette = new[]
        {
            Windows.UI.Color.FromArgb(255, 54, 111, 221),
            Windows.UI.Color.FromArgb(255, 118, 78, 207),
            Windows.UI.Color.FromArgb(255, 38, 151, 118),
            Windows.UI.Color.FromArgb(255, 223, 119, 45),
            Windows.UI.Color.FromArgb(255, 207, 71, 102)
        };
        return palette[(int)((uint)name.GetHashCode() % palette.Length)];
    }

    /// <summary>启动目标的稳定身份：LaunchPath + 激活方式（Id 在目录刷新后会变，不能做相等依据）。</summary>
    public bool SameTargetAs(ApplicationItem other) =>
        string.Equals(LaunchPath, other.LaunchPath, StringComparison.OrdinalIgnoreCase) &&
        UseAppsFolderActivation == other.UseAppsFolderActivation;
}
