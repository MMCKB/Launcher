using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace WinUI3Desktop;

public sealed partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        ConfigureWindow();
        UpdatePageCopy("Appearance");
    }

    private void ConfigureWindow()
    {
        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        appWindow.Title = "MyDock 设置";
        appWindow.Resize(new Windows.Graphics.SizeInt32(1080, 720));

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
            presenter.IsAlwaysOnTop = false;
        }
    }

    private void SettingsNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is NavigationViewItem item && item.Tag is string tag)
        {
            UpdatePageCopy(tag);
        }
    }

    private void UpdatePageCopy(string section)
    {
        (PageTitleText.Text, PageDescriptionText.Text) = section switch
        {
            "Appearance" => ("外观", "在这里管理主题、颜色与材质。"),
            "Layout" => ("布局", "在这里管理界面位置、尺寸与排列方式。"),
            "Interaction" => ("交互", "在这里管理鼠标、触屏、手势与快捷键行为。"),
            "Apps" => ("应用", "在这里管理固定应用、最近使用项与图标显示。"),
            "System" => ("系统", "在这里管理启动、更新、诊断和系统集成。"),
            _ => ("设置", "选择左侧分类以查看对应的设置页面。")
        };
    }
}
