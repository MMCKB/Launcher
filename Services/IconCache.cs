using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace WinUI3Desktop;

/// <summary>
/// 通过 Shell 提取应用图标并缓存为本地 PNG（与主流第三方启动器同一路径）：
/// 主路径 IShellItemImageFactory::GetImage(SIIGBF_ICONONLY)，直接向 Shell 请求指定位图，
/// 真实文件、.lnk 与 shell:AppsFolder 中的 UWP/Store 应用统一处理，不经过
/// SHGetImageList 图像列表索引（其索引在 PerMonitorV2 进程中不可靠）。
/// 所有失败路径都返回 null，由彩色首字母兜底，不能让图标问题影响桌面。
/// </summary>
public static class IconCache
{
    private static readonly object ExtractLock = new();
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MyDock",
        "IconCache");

    private const int IconRequestSize = 64; // 启动台瓦片约 60px，64px 源缩放清晰
    private const int SiigbfIconOnly = 0x4; // 只要图标，绝不回退缩略图

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hgdiobj, int cb, out BITMAP bm);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint startScan, uint scanLines, byte[] bits, ref BITMAPINFO bmi, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon; // BOOL
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;

    /// <summary>解析出可直接绑定的图标，失败返回 null。</summary>
    public static ImageSource? GetTileIcon(ApplicationItem item)
    {
        var path = GetCachedIconPath(item);
        if (path is null)
        {
            return null;
        }

        try
        {
            return new BitmapImage(new Uri(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>返回图标文件路径：.ico 直接用源文件，其余返回缓存 PNG；失败返回 null。</summary>
    public static string? GetCachedIconPath(ApplicationItem item)
    {
        try
        {
            var source = ResolveIconSource(item);
            if (source is null)
            {
                return null;
            }

            if (source.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) && File.Exists(source))
            {
                return source;
            }

            var cached = Path.Combine(CacheDirectory, ComputeCacheKey(source) + ".png");
            if (File.Exists(cached))
            {
                return cached;
            }

            lock (ExtractLock)
            {
                if (File.Exists(cached))
                {
                    return cached;
                }

                var png = ExtractIconPng(source);
                if (png is null)
                {
                    return null;
                }

                Directory.CreateDirectory(CacheDirectory);
                File.WriteAllBytes(cached, png);
            }

            return cached;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 归一化图标来源：IconPath/LaunchPath 优先；裸 exe 名在 System32/PATH 中解析；
    /// AUMID（UWP/Store 应用）交给 shell:AppsFolder 命名空间。
    /// </summary>
    private static string? ResolveIconSource(ApplicationItem item)
    {
        foreach (var candidate in new[] { item.IconPath, item.LaunchPath })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            // 跳过 ms-settings: 等非文件 URI
            if (candidate.Contains(':') && !Path.IsPathRooted(candidate))
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }

            if (!candidate.Contains('\\') && candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var resolved = ResolveOnPath(candidate);
                if (resolved is not null)
                {
                    return resolved;
                }
            }
        }

        if (item.UseAppsFolderActivation && !string.IsNullOrWhiteSpace(item.LaunchPath) &&
            !item.LaunchPath.Contains(':'))
        {
            return "shell:AppsFolder\\" + item.LaunchPath;
        }

        return null;
    }

    private static string? ResolveOnPath(string fileName)
    {
        var inSystem = Path.Combine(Environment.SystemDirectory, fileName);
        if (File.Exists(inSystem))
        {
            return inSystem;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                 .Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var path = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(path))
                {
                    return path;
                }
            }
            catch
            {
                // PATH 中可能存在非法条目，跳过。
            }
        }

        return null;
    }

    private static string ComputeCacheKey(string source)
    {
        // "v3" 前缀避免命中旧提取管线（48px）写入的失效缓存
        string material;
        if (source.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            material = $"v3|{source.ToLowerInvariant()}";
        }
        else
        {
            var info = new FileInfo(source);
            material = $"v3|{source.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..16];
    }

    private static byte[]? ExtractIconPng(string source)
    {
        if (TryGetShellIcon(source, out var hBitmap))
        {
            try
            {
                return ConvertHBitmapToPng(hBitmap);
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }

        // 兜底：SHGetFileInfo 拿 HICON，再取它的颜色位图
        return ExtractViaLegacyIcon(source);
    }

    private static bool TryGetShellIcon(string source, out IntPtr hBitmap)
    {
        hBitmap = IntPtr.Zero;
        try
        {
            var iid = new Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B");
            SHCreateItemFromParsingName(source, IntPtr.Zero, ref iid, out var factory);
            var hr = factory.GetImage(new NativeSize { Width = IconRequestSize, Height = IconRequestSize }, SiigbfIconOnly, out hBitmap);
            return hr == 0 && hBitmap != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    private static byte[]? ExtractViaLegacyIcon(string source)
    {
        var info = new SHINFO();
        if (SHGetFileInfo(source, 0, ref info, (uint)Marshal.SizeOf<SHINFO>(), ShgfiIcon | ShgfiLargeIcon) == IntPtr.Zero ||
            info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (!GetIconInfo(info.hIcon, out var iconInfo))
            {
                return null;
            }

            if (iconInfo.hbmMask != IntPtr.Zero)
            {
                DeleteObject(iconInfo.hbmMask);
            }

            try
            {
                return iconInfo.hbmColor == IntPtr.Zero ? null : ConvertHBitmapToPng(iconInfo.hbmColor);
            }
            finally
            {
                if (iconInfo.hbmColor != IntPtr.Zero)
                {
                    DeleteObject(iconInfo.hbmColor);
                }
            }
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    /// <summary>32bpp 预乘 alpha 的 BGRA 位图 → 还原直通 alpha → PNG。</summary>
    private static byte[]? ConvertHBitmapToPng(IntPtr hBitmap)
    {
        try
        {
            if (GetObject(hBitmap, Marshal.SizeOf<BITMAP>(), out var bm) == 0)
            {
                return null;
            }

            var width = bm.bmWidth;
            var height = Math.Abs(bm.bmHeight);
            if (width <= 0 || height == 0 || (long)width * height > 4096 * 4096)
            {
                return null;
            }

            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = width;
            bmi.bmiHeader.biHeight = -height; // 自上而下
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0; // BI_RGB

            var pixels = new byte[width * height * 4];
            var hdc = GetDC(IntPtr.Zero);
            try
            {
                if (GetDIBits(hdc, hBitmap, 0, (uint)height, pixels, ref bmi, 0) == 0)
                {
                    return null;
                }
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hdc);
            }

            UnpremultiplyAlpha(pixels);

            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            bitmap.UnlockBits(data);

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static void UnpremultiplyAlpha(byte[] pixels)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha == 0)
            {
                pixels[i] = 0;
                pixels[i + 1] = 0;
                pixels[i + 2] = 0;
            }
            else if (alpha != 255)
            {
                pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / alpha);
                pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / alpha);
                pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / alpha);
            }
        }
    }
}
