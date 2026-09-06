namespace WinUI3Desktop;

using System.Diagnostics;
using System.Runtime.InteropServices;

public static class ApplicationCatalog
{
    [ComImport]
    [Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(string appUserModelId, string arguments, int options, out uint processId);
    }

    private static readonly Guid ApplicationActivationManagerClsid = new("45BA127D-10A8-46EA-8AB7-56EA9078943C");

    /// <summary>
    /// shell:AppsFolder 中不可启动/无意义的系统宿主组件标记（同时匹配 AUMID 与显示名）。
    /// 注意不要把可打开的系统应用加进来：windows.immersivecontrolpanel（设置）、SecHealthUI（Windows 安全中心）。
    /// </summary>
    private static readonly string[] SystemAppsFolderMarkers =
    {
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "ApplicationFrameHost",
        "应用程序框架主机",
        "体验主机",
        "运行时代理",
        "LockApp",
        "AAD.BrokerPlugin",
        "AccountsControl",
        "BioEnrollment",
        "CredDialogHost",
        "CloudExperienceHost",
        "ContentDeliveryManager",
        "Win32.WebHost",
        "XboxGameCallableUI",
        "PinningConfirmationDialog",
        "CapturePicker",
        "FilePicker",
        "CallPicker",
        "PeopleExperienceHost",
        "MicrosoftEdgeDevToolsClient",
        "CBSPreview",
        "XGpuEjectDialog",
        "AudioClipboard",
        "AppRep.ChxApp",
        "AppResolverUX",
        "Microsoft.UI.Xaml"
    };

    /// <summary>纯系统基础设施进程，即使有窗口也不应出现在运行列表里。</summary>
    private static readonly HashSet<string> SystemProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost", "RuntimeBroker", "ShellExperienceHost", "StartMenuExperienceHost",
        "SearchHost", "SearchApp", "TextInputHost", "LockApp", "sihost", "dllhost", "conhost",
        "ctfmon", "WmiPrvSE", "taskhostw", "svchost", "csrss", "dwm", "lsass", "services",
        "smss", "wininit", "winlogon", "fontdrvhost", "audiodg", "spoolsv"
    };

    private static bool IsSystemAppsFolderEntry(string text)
    {
        foreach (var marker in SystemAppsFolderMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static List<ApplicationItem> GetSystemApplications()
    {
        var results = new List<ApplicationItem>();

        // 结构化排除：框架包/资源包（Microsoft.UI.Xaml、VCLibs 等应用库）不是用户可启动的应用，
        // 用 PackageManager 建家族黑名单，PowerToys Run 同款判断条件。
        var junkFamilies = GetJunkPackageFamilies();

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
                            var entryId = item.Path as string;

                            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(entryId))
                            {
                                continue;
                            }

                            var family = GetAppsFolderFamily(entryId);
                            if (family is not null && junkFamilies.Contains(family))
                            {
                                continue;
                            }

                            if (IsSystemAppsFolderEntry(entryId) || IsSystemAppsFolderEntry(name) ||
                                IsSystemToolShortcut(name, entryId) || IsUninstallerEntry(name, entryId))
                            {
                                continue;
                            }

                            AddIfMissing(results, new ApplicationItem
                            {
                                Name = name,
                                LaunchPath = entryId,
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
            .Take(500)
            .ToList();
    }

    /// <summary>框架包/资源包/无安装位置的包家族，这些不是用户可启动的应用。</summary>
    private static HashSet<string> GetJunkPackageFamilies()
    {
        var junk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var packageManager = new Windows.Management.Deployment.PackageManager();
            var userSid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? string.Empty;

            foreach (var package in packageManager.FindPackagesForUser(userSid))
            {
                try
                {
                    if (package.IsFramework || package.IsResourcePackage || package.InstalledLocation is null)
                    {
                        junk.Add(package.Id.FamilyName);
                    }
                }
                catch
                {
                    // 无法访问的包不影响整体枚举。
                }
            }
        }
        catch
        {
            // PackageManager 不可用时退化为仅按名称/标记过滤。
        }

        return junk;
    }

    private static string? GetAppsFolderFamily(string entryId)
    {
        var separator = entryId.IndexOf('!');
        return separator > 0 ? entryId[..separator] : null;
    }

    /// <summary>过滤安装器生成的卸载快捷方式（Uninstall xxx / 卸载 xxx / unins000 等）。</summary>
    private static bool IsUninstallerEntry(string name, string source)
    {
        return name.Contains("unins", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("卸载", StringComparison.Ordinal) ||
               source.Contains("unins", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>SDK/系统工具快捷方式名称前缀（WinDbg、Windows 性能工具包、Windows 工具箱等）。</summary>
    private static readonly string[] SystemToolNamePrefixes =
    {
        "WinDbg",
        "Windows Perf",
        "Windows 性能",
        "Windows 工具",
        "Windows 备份",
        "Windows 内存",
        "Windows App",
        "Windows API",
        "Windows 资源",
        "Windows 沙盒",
        "Windows Saf",
        "Windows Software"
    };

    /// <summary>这些开始菜单来源文件夹里的都是系统/调试工具，不属于日常启动器条目。</summary>
    private static readonly string[] SystemToolSourceFolders =
    {
        "\\Windows Kits\\",
        "\\Sysinternals\\",
        "\\Administrative Tools\\",
        "\\管理工具\\"
    };

    private static bool IsSystemToolShortcut(string name, string source)
    {
        foreach (var prefix in SystemToolNamePrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (source.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var folder in SystemToolSourceFolders)
            {
                if (source.Contains(folder, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 通过 IApplicationActivationManager 激活 AppsFolder 应用（UWP/Store 应用等）。
    /// 激活管理器应在 MTA 上创建，避免在 UI 线程直接激活时挂起；失败时回退 explorer shell:。
    /// </summary>
    public static void ActivateAppsFolderApplication(string appUserModelId)
    {
        Task.Run(() =>
        {
            if (TryActivateApplication(appUserModelId))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"shell:AppsFolder\\{appUserModelId}",
                    UseShellExecute = true
                });
            }
            catch
            {
                // An unavailable shortcut or executable must not break the desktop.
            }
        });
    }

    private static bool TryActivateApplication(string appUserModelId)
    {
        try
        {
            var type = Type.GetTypeFromCLSID(ApplicationActivationManagerClsid);
            if (type is null || Activator.CreateInstance(type) is not IApplicationActivationManager manager)
            {
                return false;
            }

            return manager.ActivateApplication(appUserModelId, string.Empty, 0, out _) == 0; // S_OK
        }
        catch
        {
            return false;
        }
    }

    private static void AddFallbackIfMissing(List<ApplicationItem> destination, string name, string launchPath)
    {
        if (destination.Any(item => string.Equals(item.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            return;
        }

        destination.Add(new ApplicationItem { Name = name, LaunchPath = launchPath });
    }

    public static void AddIfMissing(List<ApplicationItem> destination, ApplicationItem application)
    {
        if (destination.Any(item => item.SameTargetAs(application)))
        {
            return;
        }

        destination.Add(application);
    }

    /// <summary>
    /// 获取正在运行的应用程序列表（进程级去重）。
    /// 跳过自身进程，并捕获主窗口句柄用于“点击切换窗口”。
    /// </summary>
    public static List<ApplicationItem> GetRunningApplications(List<ApplicationItem> knownApplications)
    {
        var runningApps = new List<ApplicationItem>();
        var processes = Process.GetProcesses();
        var selfProcessId = Environment.ProcessId;

        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.Id == selfProcessId)
                    {
                        continue;
                    }

                    if (process.MainWindowHandle == IntPtr.Zero)
                        continue;

                    var processName = process.ProcessName;
                    if (SystemProcessNames.Contains(processName))
                    {
                        continue;
                    }

                    var executablePath = GetProcessPath(process);

                    if (string.IsNullOrEmpty(executablePath))
                        continue;

                    var windowHandle = process.MainWindowHandle;

                    // 检查是否是已知应用
                    var knownApp = knownApplications.FirstOrDefault(a =>
                        a.LaunchPath.EndsWith(processName, StringComparison.OrdinalIgnoreCase) ||
                        a.LaunchPath.Equals(executablePath, StringComparison.OrdinalIgnoreCase));

                    if (knownApp != null && !runningApps.Any(r => r.SameTargetAs(knownApp)))
                    {
                        knownApp.WindowHandle = windowHandle;
                        runningApps.Add(knownApp);
                    }
                    else if (knownApp == null && !runningApps.Any(r =>
                        r.LaunchPath.Equals(executablePath, StringComparison.OrdinalIgnoreCase)))
                    {
                        // 未知应用，添加到运行列表
                        runningApps.Add(new ApplicationItem
                        {
                            Name = string.IsNullOrWhiteSpace(process.MainWindowTitle) ? processName : process.MainWindowTitle,
                            LaunchPath = executablePath,
                            IconPath = executablePath,
                            WindowHandle = windowHandle
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
