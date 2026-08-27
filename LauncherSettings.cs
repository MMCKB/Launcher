using System.Text.Json;

namespace WinUI3Desktop;

public static class LauncherSettings
{
    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MyDock",
        "dock-settings.json");

    public static DockConfiguration Current { get; private set; } = Load();

    public static event EventHandler? Changed;

    public static void Save()
    {
        lock (SyncRoot)
        {
            try
            {
                var directory = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Current, JsonOptions));
            }
            catch
            {
                // Settings failures should not prevent the desktop surface from running.
            }
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void ResetMaterial()
    {
        Current.Material = DockMaterialKind.GaussianBlur;
        Current.BlurAmount = 28;
        Current.Opacity = 0.78;
        Current.TintStrength = 0.68;
        Save();
    }

    public static void ResetCornerRadius()
    {
        Current.CornerRadius = 26;
        Save();
    }

    public static void ResetDrawerGap()
    {
        Current.DrawerGap = 12;
        Save();
    }

    private static DockConfiguration Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var result = JsonSerializer.Deserialize<DockConfiguration>(File.ReadAllText(SettingsPath), JsonOptions);
                if (result is not null)
                {
                    result.DockApplications ??= new List<DockApplication>();
                    result.CustomApplications ??= new List<DockApplication>();
                    return result;
                }
            }
        }
        catch
        {
            // A broken local settings file falls back to safe defaults.
        }

        return DockConfiguration.CreateDefault();
    }
}
