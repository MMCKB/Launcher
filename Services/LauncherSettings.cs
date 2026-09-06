using System.Text.Json;

namespace WinUI3Desktop;

public static class LauncherSettings
{
    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MyDock");
    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "launcher-settings.json");
    private static readonly string LegacySettingsPath = Path.Combine(SettingsDirectory, "dock-settings.json");

    public static LauncherOptions Current { get; private set; } = Load();

    /// <summary>落盘后触发：消费方做全量刷新（应用目录/图标/外观）。</summary>
    public static event EventHandler? Changed;

    /// <summary>仅预览（滑块拖动中触发）：消费方只套用外观，不做重枚举。</summary>
    public static event EventHandler? PreviewChanged;

    public static void Save()
    {
        lock (SyncRoot)
        {
            try
            {
                Directory.CreateDirectory(SettingsDirectory);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Current, JsonOptions));
            }
            catch
            {
                // Settings failures should not prevent the desktop surface from running.
            }
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>滑块拖动中调用：只通知外观套用，不写盘。</summary>
    public static void NotifyPreviewChanged() => PreviewChanged?.Invoke(null, EventArgs.Empty);

    public static void ResetMaterial()
    {
        Current.Material = SurfaceMaterialKind.GaussianBlur;
        Current.MaterialOpacity = 0.78;
        Current.TintStrength = 0.68;
        Save();
    }

    public static void ResetCornerRadius()
    {
        Current.CornerRadius = 22;
        Save();
    }

    private static LauncherOptions Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var result = JsonSerializer.Deserialize<LauncherOptions>(File.ReadAllText(SettingsPath), JsonOptions);
                if (result is not null)
                {
                    result.PinnedApplications ??= new List<ApplicationItem>();
                    result.CustomApplications ??= new List<ApplicationItem>();
                    return result;
                }
            }
        }
        catch
        {
            // A broken local settings file falls back to safe defaults.
        }

        return TryMigrateLegacySettings() ?? LauncherOptions.CreateDefault();
    }

    /// <summary>
    /// 旧版（Dock 方案）的 dock-settings.json 迁移：主题/材质参数照搬，
    /// Dock 固定应用转入“常用”，旧的图标大小/间距等 Dock 专属配置丢弃。
    /// 旧文件保留不删，便于回退到备份版本。
    /// </summary>
    private static LauncherOptions? TryMigrateLegacySettings()
    {
        try
        {
            if (!File.Exists(LegacySettingsPath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(LegacySettingsPath));
            var root = document.RootElement;
            var options = new LauncherOptions();

            if (root.TryGetProperty("Theme", out var theme) && theme.ValueKind == JsonValueKind.Number)
            {
                options.Theme = (AppThemeKind)Math.Clamp(theme.GetInt32(), 0, 2);
            }

            if (root.TryGetProperty("Material", out var material) && material.ValueKind == JsonValueKind.Number)
            {
                options.Material = (SurfaceMaterialKind)Math.Clamp(material.GetInt32(), 0, 3);
            }

            if (root.TryGetProperty("Opacity", out var opacity) && opacity.ValueKind == JsonValueKind.Number)
            {
                options.MaterialOpacity = Math.Clamp(opacity.GetDouble(), 0.4, 1.0);
            }

            if (root.TryGetProperty("TintStrength", out var tint) && tint.ValueKind == JsonValueKind.Number)
            {
                options.TintStrength = Math.Clamp(tint.GetDouble(), 0, 1);
            }

            if (root.TryGetProperty("CornerRadius", out var corner) && corner.ValueKind == JsonValueKind.Number)
            {
                options.CornerRadius = Math.Clamp(corner.GetDouble(), 0, 40);
            }

            if (root.TryGetProperty("DockApplications", out var dockApps) && dockApps.ValueKind == JsonValueKind.Array)
            {
                options.PinnedApplications = ReadLegacyApplications(dockApps);
            }

            if (root.TryGetProperty("CustomApplications", out var customApps) && customApps.ValueKind == JsonValueKind.Array)
            {
                options.CustomApplications = ReadLegacyApplications(customApps);
            }

            return options;
        }
        catch
        {
            return null;
        }
    }

    private static List<ApplicationItem> ReadLegacyApplications(JsonElement array)
    {
        var results = new List<ApplicationItem>();
        foreach (var element in array.EnumerateArray())
        {
            try
            {
                var name = element.TryGetProperty("Name", out var nameElement) ? nameElement.GetString() : null;
                var launchPath = element.TryGetProperty("LaunchPath", out var pathElement) ? pathElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(launchPath))
                {
                    continue;
                }

                results.Add(new ApplicationItem
                {
                    Name = name,
                    LaunchPath = launchPath,
                    IconPath = element.TryGetProperty("IconPath", out var icon) ? icon.GetString() : null,
                    UseAppsFolderActivation = element.TryGetProperty("UseAppsFolderActivation", out var useApps) && useApps.ValueKind == JsonValueKind.True,
                    IsCustom = element.TryGetProperty("IsCustom", out var custom) && custom.ValueKind == JsonValueKind.True
                });
            }
            catch
            {
                // 单条坏数据跳过，不影响其余迁移。
            }
        }

        return results;
    }
}
