using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Runtime.InteropServices;
using WinRT.Interop;
using Windows.Storage.Pickers;

namespace WinUI3Desktop;

public sealed partial class SettingsWindow : Window
{
    private bool _isLoading;

    public SettingsWindow()
    {
        InitializeComponent();
        ConfigureWindow();
        LoadControlsFromSettings();
        UpdatePage("Dock");
    }

    private void ConfigureWindow()
    {
        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        // Hide system title bar and use WinUI3 custom title bar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        appWindow.Title = "MyDock 设置";
        appWindow.Resize(new Windows.Graphics.SizeInt32(1080, 760));

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
            presenter.IsAlwaysOnTop = false;
        }

        // Remove white borders by extending frame
        RemoveWhiteBorders(hWnd);

        // Handle title bar drag
        AppTitleBar.PointerPressed += AppTitleBar_PointerPressed;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int leftWidth;
        public int rightWidth;
        public int topHeight;
        public int bottomHeight;
    }

    private void RemoveWhiteBorders(IntPtr hWnd)
    {
        try
        {
            var margins = new MARGINS { leftWidth = -1, rightWidth = -1, topHeight = -1, bottomHeight = -1 };
            DwmExtendFrameIntoClientArea(hWnd, ref margins);
        }
        catch
        {
            // Ignore if not supported
        }
    }

    private void AppTitleBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(AppTitleBar).Properties.IsLeftButtonPressed)
        {
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32());
        }
    }

    private void SettingsNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is NavigationViewItem item && item.Tag is string section)
        {
            UpdatePage(section);
        }
    }

    private void UpdatePage(string section)
    {
        var isDock = section == "Dock";
        DockSettingsPanel.Visibility = isDock ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderPanel.Visibility = isDock ? Visibility.Collapsed : Visibility.Visible;

        (PageTitleText.Text, PageDescriptionText.Text) = section switch
        {
            "Dock" => ("Dock", "管理底部 Dock、应用抽屉、材质和固定应用。"),
            "Appearance" => ("外观", "在这里管理全局主题、颜色与桌面材质。"),
            "Layout" => ("布局", "在这里管理桌面图标、页面与整体排列方式。"),
            "Interaction" => ("交互", "在这里管理鼠标、触屏、手势与快捷键行为。"),
            "Apps" => ("应用", "在这里管理应用抽屉来源与图标显示。"),
            "System" => ("系统", "在这里管理启动、更新、诊断和系统集成。"),
            _ => ("设置", "选择左侧分类以查看对应的设置页面。")
        };
    }

    private void LoadControlsFromSettings()
    {
        _isLoading = true;
        var settings = LauncherSettings.Current;

        LockDockToggle.IsOn = settings.IsLocked;
        MaterialComboBox.SelectedIndex = settings.Material switch
        {
            DockMaterialKind.Acrylic => 1,
            DockMaterialKind.Mica => 2,
            DockMaterialKind.LiquidGlass => 3,
            _ => 0
        };
        BlurNumberBox.Value = settings.BlurAmount;
        OpacityNumberBox.Value = settings.Opacity;
        TintNumberBox.Value = settings.TintStrength;
        CornerRadiusNumberBox.Value = settings.CornerRadius;
        SeparateDrawerToggle.IsOn = settings.SeparateDrawer;
        DrawerGapNumberBox.Value = settings.DrawerGap;
        SearchPositionComboBox.SelectedIndex = settings.SearchAtBottom ? 1 : 0;
        DrawerRowsNumberBox.Value = settings.DrawerRows;
        DrawerColumnsNumberBox.Value = settings.DrawerColumns;
        DrawerGapPanel.Visibility = settings.SeparateDrawer ? Visibility.Visible : Visibility.Collapsed;

        UpdateMaterialParameterCopy(settings.Material);
        RefreshApplicationLists();
        _isLoading = false;
    }

    private void RefreshApplicationLists()
    {
        var availableApplications = ApplicationCatalog.GetSystemApplications();
        foreach (var custom in LauncherSettings.Current.CustomApplications)
        {
            ApplicationCatalog.AddIfMissing(availableApplications, custom);
        }

        SystemApplicationComboBox.ItemsSource = availableApplications;
        DockApplicationList.ItemsSource = LauncherSettings.Current.DockApplications.ToList();
    }

    private void Persist(Action<DockConfiguration> update)
    {
        if (_isLoading)
        {
            return;
        }

        update(LauncherSettings.Current);
        LauncherSettings.Save();
    }

    private void LockDockToggle_Toggled(object sender, RoutedEventArgs e) => Persist(settings => settings.IsLocked = LockDockToggle.IsOn);

    private void MaterialComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MaterialComboBox.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<DockMaterialKind>(tag, out var material))
        {
            UpdateMaterialParameterCopy(material);
            Persist(settings => settings.Material = material);
        }
    }

    private void BlurNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        => Persist(settings => settings.BlurAmount = Clamp(args.NewValue, 0, 100, 28));

    private void OpacityNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        => Persist(settings => settings.Opacity = Clamp(args.NewValue, 0.2, 0.98, 0.78));

    private void TintNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        => Persist(settings => settings.TintStrength = Clamp(args.NewValue, 0.1, 0.95, 0.68));

    private void CornerRadiusNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        => Persist(settings => settings.CornerRadius = Clamp(args.NewValue, 0, 48, 26));

    private void SeparateDrawerToggle_Toggled(object sender, RoutedEventArgs e)
    {
        DrawerGapPanel.Visibility = SeparateDrawerToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        Persist(settings => settings.SeparateDrawer = SeparateDrawerToggle.IsOn);
    }

    private void DrawerGapNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        => Persist(settings => settings.DrawerGap = Clamp(args.NewValue, 0, 80, 12));

    private void SearchPositionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Persist(settings => settings.SearchAtBottom = SearchPositionComboBox.SelectedIndex == 1);
    }

    private void DrawerRowsNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        => Persist(settings => settings.DrawerRows = (int)Math.Round(Clamp(args.NewValue, 1, 6, 3)));

    private void DrawerColumnsNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        => Persist(settings => settings.DrawerColumns = (int)Math.Round(Clamp(args.NewValue, 3, 10, 6)));

    private void ResetLockDock_Click(object sender, RoutedEventArgs e)
    {
        LauncherSettings.Current.IsLocked = false;
        LauncherSettings.Save();
        LoadControlsFromSettings();
    }

    private void ResetMaterial_Click(object sender, RoutedEventArgs e)
    {
        LauncherSettings.ResetMaterial();
        LoadControlsFromSettings();
    }

    private void ResetCornerRadius_Click(object sender, RoutedEventArgs e)
    {
        LauncherSettings.ResetCornerRadius();
        LoadControlsFromSettings();
    }

    private void ResetSeparateDrawer_Click(object sender, RoutedEventArgs e)
    {
        LauncherSettings.Current.SeparateDrawer = false;
        LauncherSettings.Save();
        LoadControlsFromSettings();
    }

    private void ResetDrawerGap_Click(object sender, RoutedEventArgs e)
    {
        LauncherSettings.ResetDrawerGap();
        LoadControlsFromSettings();
    }

    private void ResetSearchPosition_Click(object sender, RoutedEventArgs e)
    {
        LauncherSettings.Current.SearchAtBottom = false;
        LauncherSettings.Save();
        LoadControlsFromSettings();
    }

    private void ResetDrawerGrid_Click(object sender, RoutedEventArgs e)
    {
        LauncherSettings.Current.DrawerRows = 3;
        LauncherSettings.Current.DrawerColumns = 6;
        LauncherSettings.Save();
        LoadControlsFromSettings();
    }

    private void AddSelectedApplication_Click(object sender, RoutedEventArgs e)
    {
        if (LauncherSettings.Current.IsLocked || SystemApplicationComboBox.SelectedItem is not DockApplication application)
        {
            return;
        }

        AddApplicationToDock(application);
    }

    private async void AddCustomExe_Click(object sender, RoutedEventArgs e)
    {
        if (LauncherSettings.Current.IsLocked)
        {
            return;
        }

        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        var application = new DockApplication
        {
            Name = Path.GetFileNameWithoutExtension(file.Path),
            LaunchPath = file.Path,
            IsCustom = true
        };

        ApplicationCatalog.AddIfMissing(LauncherSettings.Current.CustomApplications, application);
        AddApplicationToDock(application);
    }

    private void RemoveSelectedApplication_Click(object sender, RoutedEventArgs e)
    {
        if (LauncherSettings.Current.IsLocked || DockApplicationList.SelectedItem is not DockApplication application)
        {
            return;
        }

        LauncherSettings.Current.DockApplications.RemoveAll(item => item.Id == application.Id);
        LauncherSettings.Save();
        RefreshApplicationLists();
    }

    private void AddApplicationToDock(DockApplication application)
    {
        if (LauncherSettings.Current.DockApplications.Any(item =>
                string.Equals(item.LaunchPath, application.LaunchPath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        LauncherSettings.Current.DockApplications.Add(application);
        LauncherSettings.Save();
        RefreshApplicationLists();
    }

    private void UpdateMaterialParameterCopy(DockMaterialKind material)
    {
        (TintParameterTitleText.Text, TintParameterDescriptionText.Text) = material switch
        {
            DockMaterialKind.GaussianBlur => ("背景混合度", "控制高斯模糊材质的颜色覆盖强度。"),
            DockMaterialKind.Acrylic => ("颜色混合度", "控制亚克力颜色与桌面背景的混合程度。"),
            DockMaterialKind.Mica => ("壁纸融合度", "控制云母材质与壁纸色彩的融合程度。"),
            DockMaterialKind.LiquidGlass => ("折射强度", "控制液态玻璃的高光与颜色折射强度。"),
            _ => ("颜色混合度", "控制材质颜色与桌面背景的混合程度。")
        };

        BlurSettingRow.Visibility = material == DockMaterialKind.Mica ? Visibility.Collapsed : Visibility.Visible;
    }

    private static double Clamp(double value, double minimum, double maximum, double fallback)
    {
        return double.IsNaN(value) ? fallback : Math.Clamp(value, minimum, maximum);
    }
}
