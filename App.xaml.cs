using Microsoft.UI.Xaml;

namespace WinUI3Desktop;

public partial class App : Application
{
    private Window? m_window;

    public App()
    {
        this.InitializeComponent();

        // 双保险：清单已声明 PerMonitorV2；这里再显式设置一次，防止清单被部署链路丢掉。
        // 缺失 DPI 感知时窗口会被 DWM 位图拉伸，文字和界面整体模糊。必须在首个窗口创建前调用。
        try
        {
            Win32.SetProcessDpiAwarenessContext(Win32.DpiAwarenessPerMonitorV2);
        }
        catch
        {
            // 已通过清单生效或系统不支持时静默忽略。
        }

        // 崩溃兜底：记录原因并把原生任务栏还回来
        UnhandledException += (_, args) =>
        {
            LogCrash("UnhandledException: " + args.Message + Environment.NewLine + args.Exception);
            MainWindow.RestoreNativeTaskbar();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            m_window = new MainWindow();
            m_window.Activate();
        }
        catch (Exception ex)
        {
            LogCrash("OnLaunched: " + ex);
            throw;
        }
    }

    /// <summary>启动失败诊断日志：%LOCALAPPDATA%\MyDock\startup-error.txt</summary>
    private static void LogCrash(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MyDock");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "startup-error.txt"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响退出流程。
        }
    }
}
