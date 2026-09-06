using System.Diagnostics;
using Microsoft.UI;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.UI;

namespace WinUI3Desktop;

/// <summary>
/// 控制中心浮层（MainWindow 的 partial）：WLAN/蓝牙开关磁贴（Radio API）+ 主音量（CoreAudio）
/// + 设备搜索视图（WLAN 用 netsh 输出解析并支持连接，蓝牙用 DeviceWatcher 实时枚举与配对）。
/// </summary>
public sealed partial class MainWindow : Window
{
    // 附加属性键因系统而异：IsConnected 部分系统未注册，创建失败时降级为最小集
    private static readonly string[] BluetoothFullProperties =
    {
        "System.ItemNameDisplay",
        "System.Devices.Aep.IsPaired",
        "System.Devices.Aep.IsConnected"
    };

    private static readonly string[] BluetoothMinimalProperties =
    {
        "System.ItemNameDisplay",
        "System.Devices.Aep.IsPaired"
    };

    private bool _controlCenterOpen;
    private bool _suppressControlEvents;
    private bool _suppressVolumeEvents;
    private bool _wifiBusy;
    private bool _bluetoothBusy;
    private bool _connecting;
    private DeviceWatcher? _bluetoothWatcher;
    private readonly List<BluetoothEntry> _bluetoothEntries = new();
    private string? _selectedWifiSsid;
    private bool _selectedWifiSecured;
    private bool _selectedWifiHasProfile;
    private Button? _selectedWifiRow;

    private sealed record BluetoothEntry(string Id, string Name, bool CanPair, bool IsConnected, DeviceInformation Info);

    private void ControlCenterButton_Click(object sender, RoutedEventArgs e)
    {
        if (_controlCenterOpen)
        {
            CloseControlCenter();
        }
        else
        {
            _ = OpenControlCenterAsync();
        }
    }

    private async Task OpenControlCenterAsync()
    {
        _controlCenterOpen = true;
        ShowMainPanel();
        ControlCenterRoot.Visibility = Visibility.Visible;
        AnimateControlCenter(true);
        await RefreshControlCenterAsync();
        _ = ScanWifiAsync();
    }

    private void CloseControlCenter()
    {
        if (!_controlCenterOpen)
        {
            return;
        }

        _controlCenterOpen = false;
        StopBluetoothWatch();
        AnimateControlCenter(false);
        _ = Task.Delay(130).ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_controlCenterOpen)
            {
                ControlCenterRoot.Visibility = Visibility.Collapsed;
            }
        }));
    }

    private void AnimateControlCenter(bool opening)
    {
        var visual = ElementCompositionPreview.GetElementVisual(ControlCenterRoot);
        var compositor = visual.Compositor;
        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0f, opening ? 0f : 1f);
        opacity.InsertKeyFrame(1f, opening ? 1f : 0f);
        opacity.Duration = TimeSpan.FromMilliseconds(opening ? 160 : 130);
        visual.StartAnimation("Opacity", opacity);

        var cardVisual = ElementCompositionPreview.GetElementVisual(ControlCenterCard);
        cardVisual.CenterPoint = new System.Numerics.Vector3(
            (float)(ControlCenterCard.ActualWidth / 2), (float)(ControlCenterCard.ActualHeight / 2), 0);
        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0f, opening ? new System.Numerics.Vector3(0.96f, 0.96f, 1f) : cardVisual.Scale);
        scale.InsertKeyFrame(1f, opening ? System.Numerics.Vector3.One : new System.Numerics.Vector3(0.97f, 0.97f, 1f));
        scale.Duration = TimeSpan.FromMilliseconds(opening ? 160 : 130);
        cardVisual.StartAnimation("Scale", scale);
    }

    private void ControlCenterRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsDescendantOfControlCenterCard(e.OriginalSource as DependencyObject))
        {
            return;
        }

        e.Handled = true;
        CloseControlCenter();
    }

    private bool IsDescendantOfControlCenterCard(DependencyObject? node)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ControlCenterCard))
            {
                return true;
            }

            node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        }

        return false;
    }

    // ---- 磁贴 ----

    private async void WifiTile_Click(object sender, RoutedEventArgs e)
    {
        if (_wifiBusy)
        {
            return;
        }

        _wifiBusy = true;
        WifiStateText.Text = "正在切换…";
        var current = await RadioController.GetStateAsync(RadioKind.WiFi);
        var result = await RadioController.SetStateAsync(RadioKind.WiFi, !current.IsOn);
        ApplyTileState(WifiTile, WifiIcon, result, IsEffectiveLightTheme(LauncherSettings.Current));
        WifiStateText.Text = DescribeRadio(result);
        _wifiBusy = false;
    }

    private async void BluetoothTile_Click(object sender, RoutedEventArgs e)
    {
        if (_bluetoothBusy)
        {
            return;
        }

        _bluetoothBusy = true;
        BluetoothStateText.Text = "正在切换…";
        var current = await RadioController.GetStateAsync(RadioKind.Bluetooth);
        var result = await RadioController.SetStateAsync(RadioKind.Bluetooth, !current.IsOn);
        ApplyTileState(BluetoothTile, BluetoothIcon, result, IsEffectiveLightTheme(LauncherSettings.Current));
        BluetoothStateText.Text = DescribeRadio(result);
        _bluetoothBusy = false;
    }

    private async Task RefreshControlCenterAsync()
    {
        _suppressControlEvents = true;
        try
        {
            var isLight = IsEffectiveLightTheme(LauncherSettings.Current);
            var wifi = await RadioController.GetStateAsync(RadioKind.WiFi);
            ApplyTileState(WifiTile, WifiIcon, wifi, isLight);
            WifiStateText.Text = DescribeRadio(wifi);

            var bluetooth = await RadioController.GetStateAsync(RadioKind.Bluetooth);
            ApplyTileState(BluetoothTile, BluetoothIcon, bluetooth, isLight);
            BluetoothStateText.Text = DescribeRadio(bluetooth);

            if (AudioController.TryGetVolume(out var volume, out var muted))
            {
                _suppressVolumeEvents = true;
                VolumeSlider.Value = volume;
                VolumeValueText.Text = $"{volume:0}";
                VolumeIcon.Glyph = muted ? "\uE74F" : "\uE767";
                _suppressVolumeEvents = false;
            }
        }
        finally
        {
            _suppressControlEvents = false;
        }
    }

    private static void ApplyTileState(Button tile, FontIcon icon, RadioController.RadioStateInfo state, bool isLight)
    {
        var isOn = state.Available && state.IsOn;
        if (isOn)
        {
            tile.Background = new SolidColorBrush(Color.FromArgb(0xE6, 45, 94, 183));
            tile.Foreground = new SolidColorBrush(Colors.White);
            icon.Foreground = new SolidColorBrush(Colors.White);
        }
        else
        {
            // 关闭态必须 ClearValue 恢复主题前景色：设为 null 会写入本地 null 值导致不渲染
            tile.Background = new SolidColorBrush(isLight
                ? Color.FromArgb(0x14, 0, 0, 0)
                : Color.FromArgb(0x22, 255, 255, 255));
            tile.ClearValue(Control.ForegroundProperty);
            icon.ClearValue(FontIcon.ForegroundProperty);
        }
    }

    private static string DescribeRadio(RadioController.RadioStateInfo state)
    {
        return !state.Available ? "不可用" : state.IsOn ? "已开启" : "已关闭";
    }

    // ---- 音量 ----

    private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressVolumeEvents)
        {
            return;
        }

        AudioController.TrySetVolume(VolumeSlider.Value);
        VolumeValueText.Text = $"{VolumeSlider.Value:0}";
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (!AudioController.TryGetVolume(out _, out var muted))
        {
            return;
        }

        AudioController.TrySetMute(!muted);
        VolumeIcon.Glyph = !muted ? "\uE74F" : "\uE767";
    }

    // ---- 视图切换 ----

    private void ShowMainPanel()
    {
        StopBluetoothWatch();
        ControlCenterMainPanel.Visibility = Visibility.Visible;
        WifiListPanel.Visibility = Visibility.Collapsed;
        BluetoothListPanel.Visibility = Visibility.Collapsed;
    }

    private void WifiEntryButton_Click(object sender, RoutedEventArgs e)
    {
        ControlCenterLog("进入 WLAN 搜索视图");
        ControlCenterMainPanel.Visibility = Visibility.Collapsed;
        BluetoothListPanel.Visibility = Visibility.Collapsed;
        WifiListPanel.Visibility = Visibility.Visible;
        _ = ScanWifiAsync();
    }

    private void BluetoothEntryButton_Click(object sender, RoutedEventArgs e)
    {
        ControlCenterLog("进入蓝牙搜索视图");
        ControlCenterMainPanel.Visibility = Visibility.Collapsed;
        WifiListPanel.Visibility = Visibility.Collapsed;
        BluetoothListPanel.Visibility = Visibility.Visible;
        StartBluetoothWatch();
    }

    private void WifiBackButton_Click(object sender, RoutedEventArgs e)
    {
        ShowMainPanel();
    }

    private void BluetoothBackButton_Click(object sender, RoutedEventArgs e)
    {
        ShowMainPanel();
    }

    private void WifiRescanButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ScanWifiAsync();
    }

    // ---- WLAN 列表（netsh 输出解析 + 连接） ----

    private async Task ScanWifiAsync()
    {
        WifiListHost.Children.Clear();
        WifiConnectArea.Visibility = Visibility.Collapsed;
        WifiListStatus.Text = "扫描中…";
        ControlCenterLog("WLAN 扫描开始");
        var connectedSsid = await Task.Run(() => WifiScanner.GetConnectedSsid());
        ControlCenterLog($"当前连接 SSID: {connectedSsid ?? "(无)"}");
        var networks = await Task.Run(() => WifiScanner.ScanViaNetsh(connectedSsid));
        ControlCenterLog($"WLAN 扫描完成: 数量={networks.Count}");
        if (networks.Count == 0)
        {
            WifiListStatus.Text = "未发现网络（确认无线网卡已启用，或点右上角刷新重试）";
            return;
        }

        // 已连接的网络置顶
        networks = networks
            .OrderByDescending(network => network.IsConnected)
            .ThenByDescending(network => network.SignalBars)
            .ThenBy(network => network.Ssid, StringComparer.OrdinalIgnoreCase)
            .ToList();

        WifiListStatus.Text = $"找到 {networks.Count} 个网络 · 点击网络进行连接";
        _selectedWifiRow = null;
        _selectedWifiSsid = null;
        foreach (var network in networks)
        {
            var row = CreateWifiRow(network);
            WifiListHost.Children.Add(row);
            if (network.IsConnected)
            {
                // 当前连接的网络置顶并高亮为蓝色
                _selectedWifiSsid = network.Ssid;
                _selectedWifiRow = row;
            }
        }
    }

    private Button CreateWifiRow(WifiScanner.WifiNetworkInfo network)
    {
        var row = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 128, 128, 128)),
            BorderThickness = new Thickness(0)
        };

        if (network.IsConnected)
        {
            // 连接中的网络：蓝色按钮 + 白字
            row.Background = new SolidColorBrush(Color.FromArgb(0xE6, 45, 94, 183));
            row.Foreground = new SolidColorBrush(Colors.White);
        }

        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

        var icon = new FontIcon { Glyph = "\uE701", FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(icon, 0);

        var namePanel = new StackPanel { Spacing = 2 };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        nameRow.Children.Add(new TextBlock { Text = network.Ssid, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        if (network.Secured)
        {
            nameRow.Children.Add(new FontIcon { Glyph = "\uE72E", FontSize = 11, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
        }

        if (network.IsConnected)
        {
            nameRow.Children.Add(new TextBlock
            {
                Text = "已连接",
                FontSize = 11,
                Foreground = new SolidColorBrush(Colors.White),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        namePanel.Children.Add(nameRow);
        namePanel.Children.Add(new TextBlock { Text = $"信号 {network.SignalBars}/4 · {(network.Secured ? "加密" : "开放")}", FontSize = 11, Opacity = 0.7 });
        Grid.SetColumn(namePanel, 1);

        var chevron = new FontIcon { Glyph = "\uE76B", FontSize = 11, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(chevron, 2);

        grid.Children.Add(icon);
        grid.Children.Add(namePanel);
        grid.Children.Add(chevron);
        row.Content = grid;

        row.Click += (_, _) => SelectWifiNetwork(network, row);
        return row;
    }

    private async void SelectWifiNetwork(WifiScanner.WifiNetworkInfo network, Button row)
    {
        if (_selectedWifiRow is not null)
        {
            _selectedWifiRow.Background = new SolidColorBrush(Color.FromArgb(0x14, 128, 128, 128));
        }

        _selectedWifiSsid = network.Ssid;
        _selectedWifiSecured = network.Secured;
        _selectedWifiRow = row;
        row.Background = new SolidColorBrush(Color.FromArgb(0x2E, 45, 94, 183));

        // 切换目标网络时清空上一次的输入与状态
        WifiPasswordBox.Text = string.Empty;
        WifiConnectStatus.Text = string.Empty;

        if (network.IsConnected)
        {
            // 已连接网络：不显示密码与连接按钮，提供断开连接
            WifiConnectTarget.Text = $"已连接: {network.Ssid}";
            WifiPasswordBox.Visibility = Visibility.Collapsed;
            WifiConnectButton.Visibility = Visibility.Collapsed;
            WifiDisconnectButton.Visibility = Visibility.Visible;
            WifiConnectArea.Visibility = Visibility.Visible;
            return;
        }

        WifiConnectTarget.Text = $"连接到: {network.Ssid}";
        WifiConnectButton.Visibility = Visibility.Visible;
        WifiDisconnectButton.Visibility = Visibility.Collapsed;
        WifiConnectArea.Visibility = Visibility.Visible;

        // 已有保存配置的网络无需再输密码（netsh wlan connect 直接用配置连接）
        _selectedWifiHasProfile = await Task.Run(() =>
        {
            var (_, profiles) = RunNetsh("wlan show profiles");
            return profiles.Contains(network.Ssid, StringComparison.OrdinalIgnoreCase);
        });
        if (!_controlCenterOpen || _selectedWifiSsid != network.Ssid)
        {
            return;
        }

        var showPassword = network.Secured && !_selectedWifiHasProfile;
        WifiPasswordBox.Visibility = showPassword ? Visibility.Visible : Visibility.Collapsed;
        if (_selectedWifiHasProfile && network.Secured)
        {
            WifiConnectStatus.Text = "将使用已保存的配置连接";
        }
    }

    private async void WifiDisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        var (ok, _) = RunNetsh("wlan disconnect");
        WifiConnectStatus.Text = ok ? "已断开连接，正在刷新列表…" : "断开失败";
        if (ok)
        {
            await Task.Delay(1500);
            _ = ScanWifiAsync();
        }
    }

    private async void WifiConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_connecting || _selectedWifiSsid is null)
        {
            return;
        }

        // 已有保存配置的网络不重复写 profile，直接用系统保存的密码连接
        var writeProfile = _selectedWifiSecured && !_selectedWifiHasProfile;
        var password = writeProfile ? WifiPasswordBox.Text : null;
        if (writeProfile && string.IsNullOrWhiteSpace(password))
        {
            WifiConnectStatus.Text = "请先输入网络密码";
            return;
        }

        _connecting = true;
        WifiConnectButton.IsEnabled = false;
        WifiPasswordBox.IsEnabled = false;
        var ssid = _selectedWifiSsid;
        WifiConnectStatus.Text = "正在连接…";
        ControlCenterLog($"WLAN 连接开始: {ssid}, 写入配置={writeProfile}");

        var ok = await Task.Run(() => ConnectWifi(ssid, password, writeProfile));
        await Task.Delay(3500);

        var (_, interfacesOutput) = RunNetsh("wlan show interfaces");
        var connected = interfacesOutput.Contains("已连接", StringComparison.Ordinal) &&
                        interfacesOutput.Contains(ssid, StringComparison.OrdinalIgnoreCase);

        ControlCenterLog($"WLAN 连接结果: ok={ok}, connected={connected}");
        WifiConnectStatus.Text = connected
            ? "已连接 ✓"
            : "连接失败（检查密码/信号，或该网络不支持自动连接）";

        _connecting = false;
        WifiConnectButton.IsEnabled = true;
        WifiPasswordBox.IsEnabled = true;

        if (connected)
        {
            _ = ScanWifiAsync();
        }
    }

    /// <summary>连接 WLAN：writeProfile 时写入 WLAN 配置文件（开放网络直连，加密网络用输入的密码 WPA2PSK/AES），
    /// 否则直接 netsh wlan connect 使用系统已保存的配置。</summary>
    private static bool ConnectWifi(string ssid, string? password, bool writeProfile)
    {
        if (writeProfile)
        {
            var open = string.IsNullOrEmpty(password);
            var auth = open ? "open" : "WPA2PSK";
            var encryption = open ? "none" : "AES";
            var keyXml = open
                ? string.Empty
                : $"<sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>{EscapeXml(password!)}</keyMaterial></sharedKey>";

            var profile = "<?xml version=\"1.0\"?>" +
                "<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">" +
                $"<name>{EscapeXml(ssid)}</name>" +
                $"<SSIDConfig><SSID><name>{EscapeXml(ssid)}</name></SSID></SSIDConfig>" +
                "<connectionType>ESS</connectionType><connectionMode>auto</connectionMode>" +
                "<MSM><security><authEncryption>" +
                $"<authentication>{auth}</authentication><encryption>{encryption}</encryption><useOneX>false</useOneX>" +
                "</authEncryption>" + keyXml + "</security></MSM></WLANProfile>";

            var path = Path.Combine(Path.GetTempPath(), "launcher-wifi-profile.xml");
            File.WriteAllText(path, profile);
            RunNetsh($"wlan add profile filename=\"{path}\" user=all");
        }

        var (ok, _) = RunNetsh($"wlan connect name=\"{EscapeXml(ssid)}\"");
        return ok;
    }

    private static (bool Ok, string Output) RunNetsh(string arguments)
    {
        try
        {
            var start = new ProcessStartInfo("netsh", arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.Default
            };
            using var process = Process.Start(start);
            if (process is null)
            {
                return (false, string.Empty);
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(8000);
            return (process.ExitCode == 0, output);
        }
        catch
        {
            return (false, string.Empty);
        }
    }

    private static string EscapeXml(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }

    // ---- 蓝牙列表（DeviceWatcher 实时枚举 + 配对） ----

    private void StartBluetoothWatch()
    {
        StopBluetoothWatch();
        BluetoothListHost.Children.Clear();
        BluetoothListStatus.Text = "搜索设备中…（附近可连接的蓝牙设备出现后会自动列出）";

        try
        {
            // 蓝牙设备必须用 AssociationEndpoint 枚举：单参数重载走 Device 类型，
            // 不支持 AEP 选择器（会抛 "Full text search is not supported"）
            // IsConnected 属性键部分系统未注册，失败时降级为最小属性集
            try
            {
                _bluetoothWatcher = DeviceInformation.CreateWatcher(
                    string.Empty,
                    BluetoothFullProperties,
                    DeviceInformationKind.AssociationEndpoint);
            }
            catch
            {
                _bluetoothWatcher = DeviceInformation.CreateWatcher(
                    string.Empty,
                    BluetoothMinimalProperties,
                    DeviceInformationKind.AssociationEndpoint);
            }
        }
        catch (Exception ex)
        {
            ControlCenterLog("创建蓝牙 Watcher 失败: " + ex.Message);
            BluetoothListStatus.Text = "此系统不支持蓝牙设备搜索";
            return;
        }

        _bluetoothWatcher.Added += (_, info) => DispatcherQueue.TryEnqueue(() =>
        {
            // 空选择器枚举的是全部关联端点，蓝牙设备的 Id 恒含 "Bluetooth#"
            if (!info.Id.Contains("bluetooth", StringComparison.OrdinalIgnoreCase) ||
                _bluetoothEntries.Any(entry => entry.Id == info.Id))
            {
                return;
            }

            var isConnected = info.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var connectedValue)
                && connectedValue is bool connectedFlag && connectedFlag;
            _bluetoothEntries.Add(new BluetoothEntry(
                info.Id,
                string.IsNullOrWhiteSpace(info.Name) ? "未知设备" : info.Name,
                info.Pairing.CanPair,
                isConnected,
                info));
            RenderBluetoothDevices();
        });
        _bluetoothWatcher.Updated += (_, update) => DispatcherQueue.TryEnqueue(() =>
        {
            var index = _bluetoothEntries.FindIndex(entry => entry.Id == update.Id);
            if (index < 0)
            {
                return;
            }

            var entry = _bluetoothEntries[index];
            var name = entry.Name;
            if (update.Properties.TryGetValue("System.ItemNameDisplay", out var value) && value is string nameString && !string.IsNullOrWhiteSpace(nameString))
            {
                name = nameString;
            }

            var isConnected = entry.IsConnected;
            if (update.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var connectedValue) && connectedValue is bool connectedFlag)
            {
                isConnected = connectedFlag;
            }

            _bluetoothEntries[index] = entry with { Name = name, CanPair = entry.Info.Pairing.CanPair, IsConnected = isConnected };
            RenderBluetoothDevices();
        });
        _bluetoothWatcher.Removed += (_, update) => DispatcherQueue.TryEnqueue(() =>
        {
            _bluetoothEntries.RemoveAll(entry => entry.Id == update.Id);
            RenderBluetoothDevices();
        });
        _bluetoothWatcher.Stopped += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (BluetoothListPanel.Visibility == Visibility.Visible)
            {
                BluetoothListStatus.Text = "搜索已中断：请确认蓝牙已开启后点返回重进";
            }
        });
        _bluetoothWatcher.Start();
    }

    private void StopBluetoothWatch()
    {
        if (_bluetoothWatcher is null)
        {
            return;
        }

        try
        {
            _bluetoothWatcher.Stop();
        }
        catch
        {
            // 观察器可能已停止。
        }

        _bluetoothWatcher = null;
    }

    private void RenderBluetoothDevices()
    {
        BluetoothListHost.Children.Clear();

        // 已连接置顶，其次已配对
        var ordered = _bluetoothEntries
            .OrderByDescending(entry => entry.IsConnected)
            .ThenByDescending(entry => entry.Info.Pairing.IsPaired)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        BluetoothListStatus.Text = ordered.Count == 0
            ? "搜索设备中…（附近可连接的蓝牙设备出现后会自动列出）"
            : $"找到 {ordered.Count} 个设备";

        foreach (var entry in ordered)
        {
            BluetoothListHost.Children.Add(CreateBluetoothRow(entry));
        }
    }

    private Button CreateBluetoothRow(BluetoothEntry entry)
    {
        var row = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 128, 128, 128)),
            BorderThickness = new Thickness(0)
        };

        // 只有"已连接"才用蓝色按钮；已配对未连接保持普通样式
        if (entry.IsConnected)
        {
            row.Background = new SolidColorBrush(Color.FromArgb(0xE6, 45, 94, 183));
            row.Foreground = new SolidColorBrush(Colors.White);
        }

        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

        var icon = new FontIcon { Glyph = "\uE702", FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(icon, 0);

        var nameText = new TextBlock { Text = entry.Name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(nameText, 1);

        var statusText = new TextBlock
        {
            Text = entry.IsConnected ? "已连接" : entry.Info.Pairing.IsPaired ? "已配对" : entry.CanPair ? "点击配对" : string.Empty,
            FontSize = 11,
            Opacity = entry.IsConnected ? 1 : 0.8,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(statusText, 2);

        grid.Children.Add(icon);
        grid.Children.Add(nameText);
        grid.Children.Add(statusText);
        row.Content = grid;

        if (entry.Info.Pairing.IsPaired)
        {
            // 已配对设备：点击取消配对（Windows 公开 API 无法只断开经典蓝牙，取消配对是等效操作）
            ToolTipService.SetToolTip(row, "点击取消配对");
            row.Click += async (_, _) =>
            {
                statusText.Text = "正在取消配对…";
                try
                {
                    var result = await entry.Info.Pairing.UnpairAsync();
                    statusText.Text = result.Status == DeviceUnpairingResultStatus.Unpaired ? "已取消配对 ✓" : "取消失败";
                    if (result.Status == DeviceUnpairingResultStatus.Unpaired)
                    {
                        RenderBluetoothDevices();
                    }
                }
                catch
                {
                    statusText.Text = "取消失败";
                }
            };
        }
        else if (entry.CanPair)
        {
            row.Click += async (_, _) =>
            {
                statusText.Text = "配对中…";
                try
                {
                    var result = await entry.Info.Pairing.PairAsync();
                    statusText.Text = result.Status == DevicePairingResultStatus.Paired ? "已配对 ✓" : "配对失败";
                    if (result.Status == DevicePairingResultStatus.Paired)
                    {
                        RenderBluetoothDevices();
                    }
                }
                catch
                {
                    statusText.Text = "配对失败";
                }
            };
        }

        return row;
    }

    /// <summary>控制中心诊断日志：%LOCALAPPDATA%\MyDock\cc-debug.txt</summary>
    private static void ControlCenterLog(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MyDock");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "cc-debug.txt"),
                $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响流程。
        }
    }
}
