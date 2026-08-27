namespace WinUI3Desktop;

public static class ApplicationCatalog
{
    public static List<DockApplication> GetSystemApplications()
    {
        var results = new List<DockApplication>();

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            var shellInstance = shellType is null ? null : Activator.CreateInstance(shellType);
            if (shellInstance is not null)
            {
                dynamic shell = shellInstance;
                dynamic appsFolder = shell.NameSpace("shell:AppsFolder");
                if (appsFolder is not null)
                {
                    dynamic items = appsFolder.Items();
                    var count = (int)items.Count;

                    for (var index = 0; index < count; index++)
                    {
                        try
                        {
                            dynamic item = items.Item(index);
                            var name = item.Name as string;
                            var appUserModelId = item.Path as string;

                            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(appUserModelId))
                            {
                                continue;
                            }

                            AddIfMissing(results, new DockApplication
                            {
                                Name = name,
                                LaunchPath = appUserModelId,
                                UseAppsFolderActivation = true
                            });
                        }
                        catch
                        {
                            // Individual Shell entries can be unavailable and are skipped.
                        }
                    }
                }
            }
        }
        catch
        {
            // Shell namespace access can be unavailable on restricted Windows configurations.
        }

        AddFallbackIfMissing(results, "资源管理器", "explorer.exe");
        AddFallbackIfMissing(results, "记事本", "notepad.exe");
        AddFallbackIfMissing(results, "设置", "ms-settings:");

        return results
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(250)
            .ToList();
    }

    private static void AddFallbackIfMissing(List<DockApplication> destination, string name, string launchPath)
    {
        if (destination.Any(item => string.Equals(item.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            return;
        }

        destination.Add(new DockApplication { Name = name, LaunchPath = launchPath });
    }

    public static void AddIfMissing(List<DockApplication> destination, DockApplication application)
    {
        if (destination.Any(item =>
                string.Equals(item.LaunchPath, application.LaunchPath, StringComparison.OrdinalIgnoreCase) &&
                item.UseAppsFolderActivation == application.UseAppsFolderActivation))
        {
            return;
        }

        destination.Add(application);
    }
}
