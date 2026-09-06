using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;

namespace WinUI3Desktop;

/// <summary>
/// WLAN 网络扫描：
/// 1) netsh 命令输出解析（无任何 API 权限依赖，最稳）；
/// 2) 失败时回退 wlanapi 原生接口。
/// </summary>
public static class WifiScanner
{
    public sealed record WifiNetworkInfo(string Ssid, int SignalBars, bool Secured, bool IsConnected);

    public static List<WifiNetworkInfo> ScanViaNetsh(string? connectedSsid = null)
    {
        try
        {
            var start = new ProcessStartInfo("netsh", "wlan show networks mode=bssid")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.Default
            };
            using var process = Process.Start(start);
            if (process is null)
            {
                return new List<WifiNetworkInfo>();
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            var results = new List<WifiNetworkInfo>();
            string? currentSsid = null;
            var secured = false;
            var signal = 0;

            foreach (var rawLine in output.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');

                var ssidMatch = Regex.Match(line, @"^\s*SSID\s*\d*\s*:\s*(.+?)\s*$");
                if (ssidMatch.Success)
                {
                    CommitNetwork(results, currentSsid, secured, signal, connectedSsid);
                    currentSsid = ssidMatch.Groups[1].Value.Trim();
                    secured = false;
                    signal = 0;
                    continue;
                }

                if (line.Contains("身份验证") || line.Contains("Authentication", StringComparison.OrdinalIgnoreCase))
                {
                    var value = line[(line.LastIndexOf(':') + 1)..].Trim();
                    secured = !(value.StartsWith("开放", StringComparison.Ordinal) ||
                                value.StartsWith("Open", StringComparison.OrdinalIgnoreCase));
                    continue;
                }

                if (line.Contains("信号") || line.Contains("Signal", StringComparison.OrdinalIgnoreCase))
                {
                    var percent = Regex.Match(line, @"(\d+)\s*%");
                    if (percent.Success)
                    {
                        signal = Math.Max(signal, int.Parse(percent.Groups[1].Value));
                    }
                }
            }

            CommitNetwork(results, currentSsid, secured, signal, connectedSsid);

            return results
                .OrderByDescending(network => network.SignalBars)
                .ThenBy(network => network.Ssid, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new List<WifiNetworkInfo>();
        }
    }

    private static void CommitNetwork(List<WifiNetworkInfo> results, string? ssid, bool secured, int signal, string? connectedSsid)
    {
        if (string.IsNullOrWhiteSpace(ssid))
        {
            return;
        }

        var connected = string.Equals(ssid, connectedSsid, StringComparison.OrdinalIgnoreCase);
        var existing = results.FindIndex(item => item.Ssid.Equals(ssid, StringComparison.OrdinalIgnoreCase));
        var info = new WifiNetworkInfo(ssid, Math.Clamp((int)Math.Round(signal / 25d), 0, 4), secured, connected);
        if (existing >= 0 && results[existing].SignalBars >= info.SignalBars)
        {
            return;
        }

        if (existing >= 0)
        {
            results[existing] = info;
        }
        else
        {
            results.Add(info);
        }
    }

    /// <summary>当前 WLAN 连接的 SSID（未连无线时为 null）。netsh interfaces 优先，NetworkInformation 兜底。</summary>
    public static string? GetConnectedSsid()
    {
        try
        {
            var start = new ProcessStartInfo("netsh", "wlan show interfaces")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.Default
            };
            using var process = Process.Start(start);
            if (process is not null)
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);

                // 逐行解析：记住最近的状态行，遇到 SSID 行且状态为已连接时命中
                string? lastState = null;
                foreach (var rawLine in output.Split('\n'))
                {
                    var line = rawLine.TrimEnd('\r');
                    if (line.Contains("状态") || line.Contains("Status", StringComparison.OrdinalIgnoreCase))
                    {
                        lastState = line[(line.LastIndexOf(':') + 1)..].Trim();
                        continue;
                    }

                    var ssidMatch = Regex.Match(line, @"^\s*SSID\s*:\s*(.+?)\s*$");
                    if (ssidMatch.Success && lastState?.Contains("已连接", StringComparison.Ordinal) == true)
                    {
                        return ssidMatch.Groups[1].Value.Trim();
                    }
                }

                if (!string.IsNullOrWhiteSpace(output))
                {
                    return null; // netsh 可用但未连接无线
                }
            }
        }
        catch
        {
            // netsh 失败时走 NetworkInformation 兜底。
        }

        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            return profile?.IsWlanConnectionProfile == true
                ? profile.WlanConnectionProfileDetails.GetConnectedSsid()
                : null;
        }
        catch
        {
            return null;
        }
    }

    // ---- wlanapi 原生回退 ----

    public static List<WifiNetworkInfo>? NativeScan()
    {
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out var handle) != 0)
            {
                return null;
            }

            try
            {
                var results = new List<WifiNetworkInfo>();
                if (WlanEnumInterfaces(handle, IntPtr.Zero, out var interfaceListPtr) != 0)
                {
                    return null;
                }

                try
                {
                    var interfaceList = Marshal.PtrToStructure<WlanInterfaceList>(interfaceListPtr);
                    var infoSize = Marshal.SizeOf<WlanInterfaceInfo>();
                    for (var i = 0; i < interfaceList.Count; i++)
                    {
                        var interfaceInfo = Marshal.PtrToStructure<WlanInterfaceInfo>(interfaceListPtr + 8 + i * infoSize);
                        if (WlanGetAvailableNetworkList(handle, ref interfaceInfo.InterfaceGuid, 3, IntPtr.Zero, out var networkListPtr) != 0)
                        {
                            continue;
                        }

                        try
                        {
                            var networkList = Marshal.PtrToStructure<WlanAvailableNetworkList>(networkListPtr);
                            var networkSize = Marshal.SizeOf<WlanAvailableNetwork>();
                            for (var n = 0; n < networkList.Count; n++)
                            {
                                var network = Marshal.PtrToStructure<WlanAvailableNetwork>(networkListPtr + 8 + n * networkSize);
                                var ssid = ReadSsid(network.SsidBytes.Bytes);
                                if (string.IsNullOrWhiteSpace(ssid))
                                {
                                    continue; // 隐藏网络不显示（连接需手动输入 SSID）
                                }

                                results.Add(new WifiNetworkInfo(
                                    ssid,
                                    Math.Clamp((int)Math.Round(network.SignalQuality / 25d), 0, 4),
                                    network.SecurityEnabled,
                                    string.Equals(ssid, GetConnectedSsid(), StringComparison.OrdinalIgnoreCase)));
                            }
                        }
                        finally
                        {
                            WlanFreeMemory(networkListPtr);
                        }
                    }
                }
                finally
                {
                    WlanFreeMemory(interfaceListPtr);
                }

                return results
                    .GroupBy(network => network.Ssid, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderByDescending(network => network.SignalBars).First())
                    .OrderByDescending(network => network.SignalBars)
                    .ThenBy(network => network.Ssid, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            finally
            {
                WlanCloseHandle(handle, IntPtr.Zero);
            }
        }
        catch
        {
            return null;
        }
    }

    private static string ReadSsid(byte[] ssidBytes)
    {
        var length = 0;
        while (length < ssidBytes.Length && ssidBytes[length] != 0)
        {
            length++;
        }

        return Encoding.ASCII.GetString(ssidBytes, 0, length);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanInterfaceList
    {
        public uint Count;
        public uint Index;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Description;
        public uint State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanAvailableNetworkList
    {
        public uint Count;
        public uint Index;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Dot11Ssid
    {
        public uint Length;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanAvailableNetwork
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string ProfileName;
        public Dot11Ssid SsidBytes;
        public uint BssType;
        public uint NumberOfBssIds;
        public uint DefaultAuthAlgorithm;
        public uint DefaultCipherAlgorithm;
        public uint SignalQuality;
        [MarshalAs(UnmanagedType.Bool)]
        public bool SecurityEnabled;
        public uint Flags;
        public uint Reserved;
    }

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);

    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);

    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern int WlanGetAvailableNetworkList(IntPtr handle, ref Guid interfaceGuid, uint flags, IntPtr reserved, out IntPtr networkList);
}
