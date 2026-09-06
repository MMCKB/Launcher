using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;

namespace WinUI3Desktop;

/// <summary>
/// Fluent 风格设置窗口：Mica 背景 + NavigationView + 卡片式设置项。
/// 所有改动即时生效（写盘触发 Changed，由主窗口套用）；滑块拖动中只更新内存，松手落盘。
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string AutoStartValueName = "MyDockLauncher";

    private bool _loading = true;

    public SettingsWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();

        // 跟随所选主题（跟随系统时用应用默认）
        RootGrid.RequestedTheme = LauncherSettings.Current.Theme switch
        {
            AppThemeKind.Light => ElementTheme.Light,
            AppThemeKind.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        if (AppWindow.GetFromWindowId(windowId) is { } appWindow)
        {
            appWindow.Title = "启动台设置";
            var area = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest);
            const int width = 1080;
            const int height = 720;
            appWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                area.WorkArea.X + Math.Max(0, (area.WorkArea.Width - width) / 2),
                area.WorkArea.Y + Math.Max(0, (area.WorkArea.Height - height) / 2),
                width,
                height));
        }

        Nav.SelectedItem = Nav.MenuItems[0];
        WireRadioGroup(ThemeButtons, index => LauncherSettings.Current.Theme = (AppThemeKind)index);
        WireRadioGroup(MaterialButtons, index => LauncherSettings.Current.Material = (SurfaceMaterialKind)index);
        WireRadioGroup(SwipeButtons, index => LauncherSettings.Current.BottomSwipe = (SwipeGestureMode)index);
        LoadValues();
        _loading = false;

        // 主题/材质变更即时反映到设置窗口自身（含滑块预览路径）
        LauncherSettings.Changed += OnLauncherSettingsChanged;
        LauncherSettings.PreviewChanged += OnLauncherSettingsChanged;
        Closed += (_, _) =>
        {
            LauncherSettings.Changed -= OnLauncherSettingsChanged;
            LauncherSettings.PreviewChanged -= OnLauncherSettingsChanged;
        };
    }

    private void OnLauncherSettingsChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            RootGrid.RequestedTheme = LauncherSettings.Current.Theme switch
            {
                AppThemeKind.Light => ElementTheme.Light,
                AppThemeKind.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        });
    }

    // ---- 单选组辅助（卡片里用横排 RadioButton，需自行映射序号） ----

    private void WireRadioGroup(StackPanel panel, Action<int> apply)
    {
        for (var index = 0; index < panel.Children.Count; index++)
        {
            if (panel.Children[index] is not RadioButton button)
            {
                continue;
            }

            var captured = index;
            button.Checked += (_, _) =>
            {
                if (_loading)
                {
                    return;
                }

                apply(captured);
                SaveOptions();
            };
        }
    }

    private static void SelectRadio(StackPanel panel, int index)
    {
        for (var i = 0; i < panel.Children.Count; i++)
        {
            if (panel.Children[i] is RadioButton button)
            {
                button.IsChecked = i == index;
            }
        }
    }

    private void LoadValues()
    {
        var options = LauncherSettings.Current;
        SelectRadio(ThemeButtons, Math.Clamp((int)options.Theme, 0, 2));
        SelectRadio(MaterialButtons, Math.Clamp((int)options.Material, 0, 3));
        SelectRadio(SwipeButtons, Math.Clamp((int)options.BottomSwipe, 0, 2));
        CornerRadiusSlider.Value = Math.Clamp(options.CornerRadius, 0, 40);
        OpacitySlider.Value = Math.Clamp(options.MaterialOpacity, 0.55, 1);
        TintSlider.Value = Math.Clamp(options.TintStrength, 0, 1);
        TileSizeSlider.Value = Math.Clamp(options.TileSize, 84, 128);
        ShowLauncherButtonToggle.IsOn = options.ShowLauncherButton;
        TakeOverDesktopToggle.IsOn = options.TakeOverDesktop;
        AutoStartToggle.IsOn = IsAutoStartEnabled();
        UpdateSliderLabels();
    }

    private void UpdateSliderLabels()
    {
        CornerRadiusValue.Text = $"{CornerRadiusSlider.Value:0}";
        OpacityValue.Text = $"{OpacitySlider.Value * 100:0}%";
        TintValue.Text = $"{TintSlider.Value * 100:0}%";
        TileSizeValue.Text = $"{TileSizeSlider.Value:0}";
    }

    // ---- 导航 ----

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        AppearancePane.Visibility = tag == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        LaunchpadPane.Visibility = tag == "Launchpad" ? Visibility.Visible : Visibility.Collapsed;
        SystemPane.Visibility = tag == "System" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- 外观 ----

    private void CornerRadiusSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        LauncherSettings.Current.CornerRadius = CornerRadiusSlider.Value;
        CornerRadiusValue.Text = $"{CornerRadiusSlider.Value:0}";
        LauncherSettings.NotifyPreviewChanged();
    }

    private void OpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        LauncherSettings.Current.MaterialOpacity = OpacitySlider.Value;
        OpacityValue.Text = $"{OpacitySlider.Value * 100:0}%";
        LauncherSettings.NotifyPreviewChanged();
    }

    private void TintSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        LauncherSettings.Current.TintStrength = TintSlider.Value;
        TintValue.Text = $"{TintSlider.Value * 100:0}%";
        LauncherSettings.NotifyPreviewChanged();
    }

    private void ResetMaterial_Click(object sender, RoutedEventArgs e)
    {
        LauncherSettings.ResetMaterial();
        LoadValues();
    }

    private void ResetCornerRadius_Click(object sender, RoutedEventArgs e)
    {
        LauncherSettings.ResetCornerRadius();
        LoadValues();
    }

    // ---- 启动台 ----

    private void TileSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        LauncherSettings.Current.TileSize = TileSizeSlider.Value;
        TileSizeValue.Text = $"{TileSizeSlider.Value:0}";
        LauncherSettings.NotifyPreviewChanged();
    }

    private void ShowLauncherButtonToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        LauncherSettings.Current.ShowLauncherButton = ShowLauncherButtonToggle.IsOn;
        SaveOptions();
    }

    // ---- 系统 ----

    private void TakeOverDesktopToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        LauncherSettings.Current.TakeOverDesktop = TakeOverDesktopToggle.IsOn;
        SaveOptions();
    }

    private void AutoStartToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SetAutoStart(AutoStartToggle.IsOn);
    }

    private static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(AutoStartValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static void SetAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled)
            {
                key.SetValue(AutoStartValueName, $"\"{Environment.ProcessPath}\"");
            }
            else
            {
                key.DeleteValue(AutoStartValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 注册表不可写时静默失败，不影响其他设置。
        }
    }

    // ---- 公共 ----

    /// <summary>滑块拖动中只更新内存，松手（捕获释放）后落盘，避免逐帧写 JSON。</summary>
    private void Slider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SaveOptions();
    }

    private void SaveOptions() => LauncherSettings.Save();
}
