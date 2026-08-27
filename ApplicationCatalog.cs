namespace WinUI3Desktop;

public static class ApplicationCatalog
{
    public static List<DockApplication> GetSystemApplications()
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

                    AddIfMissing(results, new DockApplication { Name = name, LaunchPath = shortcut });
                }
            }
            catch
            {
                // Protected or unavailable Start Menu folders are skipped safely.
            }
        }

        return results
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(180)
            .ToList();
    }

    public static void AddIfMissing(List<DockApplication> destination, DockApplication application)
    {
        if (destination.Any(item => string.Equals(item.LaunchPath, application.LaunchPath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        destination.Add(application);
    }
}
