namespace WinUI3Desktop;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public static class ApplicationCatalog
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHINFO psfi, uint cbFileInfo, uint uFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

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

    /// <summary>
    /// 获取正在运行的应用程序列表
    /// </summary>
    public static List<DockApplication> GetRunningApplications(List<DockApplication> knownApplications)
    {
        var runningApps = new List<DockApplication>();
        var processes = Process.GetProcesses();

        try
        {
            var knownPaths = new HashSet<string>(
                knownApplications.Select(a => a.LaunchPath.ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);

            foreach (var process in processes)
            {
                try
                {
                    if (process.MainWindowHandle == IntPtr.Zero)
                        continue;

                    var processName = process.ProcessName;
                    var executablePath = GetProcessPath(process);

                    if (string.IsNullOrEmpty(executablePath))
                        continue;

                    // 检查是否是已知应用
                    var knownApp = knownApplications.FirstOrDefault(a =>
                        a.LaunchPath.EndsWith(processName, StringComparison.OrdinalIgnoreCase) ||
                        a.LaunchPath.Equals(executablePath, StringComparison.OrdinalIgnoreCase));

                    if (knownApp != null && !runningApps.Any(r => r.Id == knownApp.Id))
                    {
                        knownApp.IsRunning = true;
                        runningApps.Add(knownApp);
                    }
                    else if (knownApp == null && !runningApps.Any(r =>
                        r.LaunchPath.Equals(executablePath, StringComparison.OrdinalIgnoreCase)))
                    {
                        // 未知应用，添加到运行列表
                        runningApps.Add(new DockApplication
                        {
                            Name = process.MainWindowTitle ?? processName,
                            LaunchPath = executablePath,
                            IsRunning = true,
                            IconPath = executablePath
                        });
                    }
                }
                catch
                {
                    // 跳过无法访问的进程
                }
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        return runningApps;
    }

    private static string? GetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }
}
