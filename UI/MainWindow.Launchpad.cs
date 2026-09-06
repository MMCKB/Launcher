using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Numerics;

namespace WinUI3Desktop;

/// <summary>
/// 启动台交互层（MainWindow 的 partial）：呼出/收起、常用/全部应用/运行中的渲染、
/// 瓦片右键菜单、运行窗口切换、底部上滑手势。
/// </summary>
public sealed partial class MainWindow : Window
{
    private enum LaunchSource
    {
        Button,
        Gesture,
        Menu,
        HotKey
    }

    private bool _launchpadOpen;
    private bool _gestureConsumed;
    private bool _bottomSwipeEnabled;

    // ---- 呼出与收起 ----

    private void LauncherButton_Click(object sender, RoutedEventArgs e)
    {
        if (_launchpadOpen)
        {
            HideLaunchpad();
        }
        else
        {
            ShowLaunchpad(LaunchSource.Button);
        }
    }

    private void ToggleLaunchpad() => ToggleLaunchpad(LaunchSource.HotKey);

    private void ToggleLaunchpad(LaunchSource source)
    {
        if (_launchpadOpen)
        {
            HideLaunchpad();
        }
        else
        {
            ShowLaunchpad(source);
        }
    }

    private void ShowLaunchpad(LaunchSource source)
    {
        if (_launchpadOpen)
        {
            return;
        }

        _launchpadOpen = true;
        LaunchpadSearch.Text = string.Empty;
        RenderLaunchpad();
        LaunchpadRoot.Visibility = Visibility.Visible;
        AnimateLaunchpad(true);

        // 键盘路径（热键/右键菜单）自动聚焦搜索框；按钮/手势路径不抢焦点，避免触屏拉起软键盘
        if (source is LaunchSource.HotKey or LaunchSource.Menu)
        {
            LaunchpadSearch.Focus(FocusState.Programmatic);
        }
    }

    private void HideLaunchpad()
    {
        if (!_launchpadOpen)
        {
            return;
        }

        _launchpadOpen = false;
        AnimateLaunchpad(false);

        // 等收起动画播完再隐藏层，避免动画被立即裁断
        _ = Task.Delay(150).ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_launchpadOpen)
            {
                LaunchpadRoot.Visibility = Visibility.Collapsed;
            }
        }));
    }

    private void LaunchpadRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 点击启动台空白处（材质背景本身）关闭，命中瓦片/搜索框等控件时不处理
        if (!ReferenceEquals(e.OriginalSource, LaunchpadMaterialHost))
        {
            return;
        }

        e.Handled = true;
        HideLaunchpad();
    }

    private void AnimateLaunchpad(bool opening)
    {
        var visual = ElementCompositionPreview.GetElementVisual(LaunchpadRoot);
        var compositor = visual.Compositor;
        var size = LaunchpadRoot.ActualSize;
        if (size.X > 0 && size.Y > 0)
        {
            visual.CenterPoint = new Vector3(size.X / 2f, size.Y / 2f, 0f);
        }

        var duration = TimeSpan.FromMilliseconds(opening ? 220 : 150);
        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0f, opening ? 0f : 1f);
        opacity.InsertKeyFrame(1f, opening ? 1f : 0f);
        opacity.Duration = duration;

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0f, opening ? new Vector3(1.05f, 1.05f, 1f) : visual.Scale);
        scale.InsertKeyFrame(1f, opening ? Vector3.One : new Vector3(0.97f, 0.97f, 1f));
        scale.Duration = duration;

        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Scale", scale);
    }

    // ---- 搜索与渲染 ----

    private void LaunchpadSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        RenderLaunchpad();
    }

    private void RenderLaunchpad()
    {
        var query = LaunchpadSearch.Text?.Trim() ?? string.Empty;
        var searching = query.Length > 0;

        var pinned = LauncherSettings.Current.PinnedApplications;
        PinnedSection.Visibility = searching || pinned.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PinnedRepeater.ItemsSource = pinned.ToList();

        var results = searching
            ? _allApplications.Where(item => item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList()
            : _allApplications.ToList();
        AllAppsHeader.Text = searching ? $"搜索结果 · {results.Count}" : "全部应用";
        AppsRepeater.ItemsSource = results;
    }

    private void RenderRunningSection(List<ApplicationItem> runningItems)
    {
        RunningStrip.Children.Clear();
        RunningSection.Visibility = runningItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var item in runningItems)
        {
            RunningStrip.Children.Add(CreateRunningTile(item));
        }
    }

    // ---- 瓦片构建 ----

    internal Button CreateAppTile(ApplicationItem item)
    {
        var tileSize = GetTileSize();
        var button = new Button
        {
            Tag = item,
            Width = tileSize,
            Height = tileSize + 42,
            Style = (Style)RootGrid.Resources["AppTileButtonStyle"]
        };
        ToolTipService.SetToolTip(button, item.Name);
        button.Content = BuildTileVisual(item, tileSize, item.IsRunning);
        button.Click += AppTile_Click;
        button.RightTapped += AppTile_RightTapped;
        AttachHoverMagnification(button, tileSize, tileSize + 42);
        return button;
    }

    private static UIElement BuildTileVisual(ApplicationItem item, double tileSize, bool showRunningDot)
    {
        var iconSize = Math.Round(tileSize * 0.62);
        var tile = new Border
        {
            Width = tileSize,
            Height = tileSize,
            CornerRadius = new CornerRadius(Math.Round(tileSize * 0.24)),
            Background = new SolidColorBrush(item.AccentColor)
        };

        var iconImage = TryLoadApplicationIcon(item, iconSize);
        if (iconImage != null)
        {
            tile.Background = null;
            tile.Child = iconImage;
        }
        else
        {
            tile.Child = new TextBlock
            {
                Text = item.Initial,
                FontSize = Math.Round(tileSize * 0.4),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        var iconHost = new Grid { Width = tileSize, Height = tileSize };
        iconHost.Children.Add(tile);
        if (showRunningDot)
        {
            iconHost.Children.Add(new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 100, 220, 120)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 6, 6)
            });
        }

        var root = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        root.Children.Add(iconHost);
        root.Children.Add(new TextBlock
        {
            Text = item.Name,
            MaxWidth = tileSize + 8,
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        });
        return root;
    }

    private Button CreateRunningTile(ApplicationItem item)
    {
        var button = new Button
        {
            Tag = item,
            Width = 96,
            Height = 88,
            Style = (Style)RootGrid.Resources["AppTileButtonStyle"]
        };
        ToolTipService.SetToolTip(button, item.Name + " (正在运行)");

        var tile = new Border
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(item.AccentColor)
        };
        var iconImage = TryLoadApplicationIcon(item, 40);
        if (iconImage != null)
        {
            tile.Background = null;
            tile.Child = iconImage;
        }
        else
        {
            tile.Child = new TextBlock
            {
                Text = item.Initial,
                FontSize = 18,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        var iconHost = new Grid { Width = 48, Height = 48 };
        iconHost.Children.Add(tile);
        iconHost.Children.Add(new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 100, 220, 120)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 2, 2)
        });

        var root = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        root.Children.Add(iconHost);
        root.Children.Add(new TextBlock
        {
            Text = item.Name,
            MaxWidth = 90,
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        });

        button.Content = root;
        button.Click += RunningTile_Click;
        button.RightTapped += RunningTile_RightTapped;
        AttachHoverMagnification(button, 96, 88);
        return button;
    }

    // ---- 瓦片交互 ----

    private void AppTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ApplicationItem item })
        {
            LaunchApplication(item);
        }
    }

    private void AppTile_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not Button { Tag: ApplicationItem item })
        {
            return;
        }

        e.Handled = true;
        var isPinned = LauncherSettings.Current.PinnedApplications.Any(p => p.SameTargetAs(item));
        var menu = new MenuFlyout();

        var pin = new MenuFlyoutItem
        {
            Text = isPinned ? "从常用移除" : "固定到常用",
            Icon = new FontIcon { Glyph = isPinned ? "\uE77A" : "\uE718" }
        };
        pin.Click += (_, _) => TogglePinned(item);
        menu.Items.Add(pin);

        var open = new MenuFlyoutItem { Text = "打开", Icon = new FontIcon { Glyph = "\uE8A7" } };
        open.Click += (_, _) => LaunchApplication(item);
        menu.Items.Add(open);

        var reveal = new MenuFlyoutItem { Text = "打开文件所在位置", Icon = new FontIcon { Glyph = "\uE838" } };
        reveal.Click += (_, _) => RevealInShell(item);
        menu.Items.Add(reveal);

        menu.Items.Add(new MenuFlyoutSeparator());
        var uninstall = new MenuFlyoutItem { Text = "卸载", Icon = new FontIcon { Glyph = "\uE74D" } };
        uninstall.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = "ms-settings:appsfeatures", UseShellExecute = true });
            }
            catch
            {
                // 打不开设置页不影响启动台。
            }
        };
        menu.Items.Add(uninstall);

        menu.ShowAt((FrameworkElement)sender, new FlyoutShowOptions { Position = e.GetPosition((UIElement)sender) });
    }

    private void RunningTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ApplicationItem item })
        {
            ActivateRunningItem(item);
        }
    }

    private void RunningTile_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not Button { Tag: ApplicationItem item })
        {
            return;
        }

        e.Handled = true;
        var menu = new MenuFlyout();
        var activate = new MenuFlyoutItem { Text = "切换到此应用", Icon = new FontIcon { Glyph = "\uE8A7" } };
        activate.Click += (_, _) => ActivateRunningItem(item);
        menu.Items.Add(activate);
        menu.ShowAt((FrameworkElement)sender, new FlyoutShowOptions { Position = e.GetPosition((UIElement)sender) });
    }

    /// <summary>点击运行中条目：恢复最小化窗口并切前台；无句柄则退回启动。</summary>
    private static void ActivateRunningItem(ApplicationItem item)
    {
        if (item.WindowHandle != IntPtr.Zero)
        {
            if (Win32.IsIconic(item.WindowHandle))
            {
                Win32.ShowWindow(item.WindowHandle, Win32.SwRestore);
            }

            Win32.SetForegroundWindow(item.WindowHandle);
            return;
        }

        LaunchApplication(item);
    }

    private void TogglePinned(ApplicationItem item)
    {
        var pinned = LauncherSettings.Current.PinnedApplications;
        if (pinned.RemoveAll(p => p.SameTargetAs(item)) == 0)
        {
            pinned.Add(item);
        }

        LauncherSettings.Save();
    }

    private static void RevealInShell(ApplicationItem item)
    {
        try
        {
            if (item.UseAppsFolderActivation && !item.LaunchPath.Contains(':'))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"shell:AppsFolder\\{item.LaunchPath}",
                    UseShellExecute = true
                });
            }
            else if (File.Exists(item.LaunchPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{item.LaunchPath}\"",
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // 无法定位的外壳条目直接忽略。
        }
    }

    // ---- 触屏手势 ----

    private void RefreshGestureState()
    {
        _bottomSwipeEnabled = LauncherSettings.Current.BottomSwipe switch
        {
            SwipeGestureMode.Enabled => true,
            SwipeGestureMode.Disabled => false,
            _ => Win32.GetSystemMetrics(Win32.SmMaximumTouches) > 0
        };
    }

    private void BottomEdgeZone_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        if (!_bottomSwipeEnabled || _gestureConsumed || _launchpadOpen)
        {
            return;
        }

        var translation = e.Cumulative.Translation;
        if (translation.Y < -72 && Math.Abs(translation.X) < 160)
        {
            _gestureConsumed = true;
            e.Handled = true;
            e.Complete();
            ShowLaunchpad(LaunchSource.Gesture);
        }
    }

    private void BottomEdgeZone_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        _gestureConsumed = false;
    }

    // ---- 状态时钟 ----

    private void ClockChip_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(8) };
        panel.Children.Add(new TextBlock
        {
            Text = DateTime.Now.ToString("HH:mm:ss"),
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = DateTime.Now.ToString("yyyy年M月d日 dddd"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xB0, 128, 128, 128))
        });

        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.Bottom };
        flyout.ShowAt(ClockChip);
    }
}

/// <summary>启动台瓦片元素工厂：尺寸与事件全部在代码里生成，避免 XAML 模板对设置的僵化绑定。</summary>
internal sealed class AppTileElementFactory : IElementFactory
{
    private readonly MainWindow _owner;

    public AppTileElementFactory(MainWindow owner)
    {
        _owner = owner;
    }

    public UIElement GetElement(ElementFactoryGetArgs args)
    {
        return args.Data is ApplicationItem item
            ? _owner.CreateAppTile(item)
            : new StackPanel();
    }

    public void RecycleElement(ElementFactoryRecycleArgs args)
    {
        // 元素随 ItemsSource 重置整体重建，不做复用。
    }
}
