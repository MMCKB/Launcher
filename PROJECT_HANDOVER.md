# Launcher 项目交接文档

## 1. 项目概述

**Launcher** 是一个第三方 Windows 桌面应用，旨在替代或增强 Windows 桌面体验。使用 C#、.NET 10、WinUI 3 和 Windows App SDK 开发。

**当前状态：** 基础框架已完成，Dock 栏和部分设置功能可用。

## 2. 技术栈

- **语言：** C#
- **框架：** .NET 10.0
- **UI框架：** WinUI 3
- **SDK：** Windows App SDK 1.8.260710003
- **平台目标：** Windows 10.0.19041.0+ (支持 x86/x64/ARM64)
- **包管理：** NuGet

## 3. 项目结构

```
WinUI3Desktop/
├── .github/
│   └── workflows/
│       └── build.yml          # GitHub Actions 云编译配置（手动触发）
├── Assets/                     # 应用图标占位文件
├── obj/                        # 构建中间文件
├── Properties/                 # 项目属性
├── WinUI3Desktop.csproj        # 项目文件
├── WinUI3Desktop.sln           # 解决方案文件
├── App.manifest                # 应用清单
├── App.xaml / App.xaml.cs      # 应用入口
├── MainWindow.xaml             # 主窗口 XAML（Dock + 右键菜单）
├── MainWindow.xaml.cs          # 主窗口逻辑（核心代码）
├── SettingsWindow.xaml         # 设置窗口 XAML
├── SettingsWindow.xaml.cs      # 设置窗口逻辑
├── DockConfiguration.cs        # 配置模型（DockApplication, DockConfiguration, DockMaterialKind）
├── LauncherSettings.cs         # 设置管理器（保存/加载配置）
└── ApplicationCatalog.cs       # 应用目录（Shell API 获取已安装应用 + 运行中应用检测）
```

## 4. 核心功能实现

### 4.1 全屏无边框窗口 (MainWindow.xaml.cs)

**位置：** `MainWindow.xaml.cs` → `ConfigureDesktopWindow()` 方法

**实现要点：**
- 使用 Win32 API (`SetWindowLong`, `SetWindowPos`) 移除窗口边框
- 使用 `DwmExtendFrameIntoClientArea` 扩展 DWM 帧到整个客户区
- 使用 `DwmSetWindowAttribute` 禁用非客户区渲染去除白边
- 覆盖整个屏幕（包括任务栏区域）
- `IsAlwaysOnTop = true` 保持在最前

**关键常量：**
```csharp
private const int GWL_STYLE = -16;
private const int WS_BORDER = 0x00800000;
private const int WS_DLGFRAME = 0x00400000;
private const int WS_THICKFRAME = 0x00040000;
```

### 4.2 系统壁纸背景

**位置：** `MainWindow.xaml.cs` → `ReloadWallpaper()` 方法

**实现：**
- 使用 `SystemParametersInfo(SPI_GETDESKWALLPAPER)` 获取壁纸路径
- 将壁纸图片添加到 `RootGrid.Children`（不替换 Grid，保留事件处理器）

### 4.3 Acrylic 右键菜单

**位置：** `MainWindow.xaml.cs` → `InitializeContextMenu()` 方法

**功能：**
- 纯代码创建 `MenuFlyout`（不是 XAML ContextFlyout）
- Acrylic 材质背景（通过 `MenuFlyoutPresenterStyle`）
- 弹出/关闭动画（150ms 缩放+淡入淡出）
- 悬停效果（100ms）

### 4.4 Dock 栏

**位置：** `MainWindow.xaml.xaml` 中的 `DockHost` 区域

**功能：**
- 显示固定应用（左）和运行中应用（右）
- 分隔线区分不同区域
- 点击启动应用，右键显示菜单
- 拖拽支持（从应用抽屉添加应用）
- 每2秒刷新运行中应用列表

### 4.5 应用图标加载

**位置：** `MainWindow.xaml.cs` → `CreateDockTile()` 和 `TryLoadApplicationIcon()` 方法

**当前实现：**
- `.ico` 文件：直接用 `BitmapImage` 加载
- `.exe` 文件：暂不支持（需要 System.Drawing.Common）
- 无图标：显示彩色背景 + 首字母

**待解决：** 无法从 .exe 提取图标（需要添加 `System.Drawing.Common` 或使用 WIC API）

### 4.6 运行中应用监听

**位置：** `ApplicationCatalog.cs` → `GetRunningApplications()` 方法

**实现：**
- 使用 `Process.GetProcesses()` 获取所有进程
- 筛选有主窗口的进程
- 匹配已知应用或创建新条目
- 定时器每2秒刷新

### 4.7 设置窗口 (SettingsWindow)

**位置：** `SettingsWindow.xaml` + `SettingsWindow.xaml.cs`

**当前状态：**
- 使用系统按钮（最小化/最大化/关闭），自定义标题栏不显示按钮
- 使用 NavigationView 作为设置导航
- Mica/Acrylic 背景（DWM API 方式，可能在某些系统版本不生效）
- 设置页面包括：外观、Dock、布局、交互、应用、系统

**重要：** 设置窗口背景使用 DWM API 设置 Mica，如果系统不支持会回退到 Acrylic。如果 Acrylic 也不显示，可能需要在 XAML 中使用 `AcrylicBrush`。

## 5. 配置系统

### 5.1 设置文件位置

**路径：** `%LOCALAPPDATA%\LauncherSettings\settings.json`

### 5.2 DockApplication 模型

```csharp
public sealed class DockApplication
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string LaunchPath { get; set; }
    public string? IconPath { get; set; }      // .ico 文件路径
    public bool UseAppsFolderActivation { get; set; }
    public bool IsCustom { get; set; }
    public bool IsRunning { get; set; }
}
```

### 5.3 配置保存/加载

**位置：** `LauncherSettings.cs`

**方法：**
- `Load()` - 从 JSON 文件加载配置
- `Save()` - 保存配置到 JSON 文件
- `LauncherSettings.Changed` - 配置变更事件

## 6. 待完成任务

### 6.1 🔴 高优先级

1. **应用图标提取**
   - 当前：无法从 .exe 提取图标
   - 方案A：添加 `System.Drawing.Common` NuGet 包，使用 `Icon.FromHandle`
   - 方案B：使用 WIC API 或 Direct2D 提取（无需额外包）
   - 方案C：调用 `SHGetFileInfo` 获取图标句柄

2. **应用启动功能**
   - 当前：点击 Dock 应用会尝试启动，但需要验证所有应用类型
   - 需要处理：
     - 标准 .exe 文件（直接启动）
     - UWP/Store 应用（使用 `Launcher.LaunchUriAsync` 或 Shell 激活）
     - 特殊 URI（如 `ms-settings:`）

3. **应用抽屉完善**
   - 当前：XAML 中有 `DrawerRepeater`，但搜索/分页功能需要完善
   - 需要验证：应用列表加载、搜索过滤、分页显示

### 6.2 🟡 中优先级

1. **设置页面内容**
   - 大部分设置页面还是空的（占位符）
   - 需要实现：外观设置、Dock 设置、布局设置、交互设置、应用设置、系统设置

2. **右键菜单完善**
   - 当前：右键菜单项较少
   - 建议添加：打开文件所在位置、以管理员身份运行、固定到任务栏、窗口管理（最大化/最小化/关闭）

3. **多显示器支持**
   - 当前：仅支持主显示器全屏
   - 需要：检测多显示器、选择显示哪个显示器、每个显示器独立 Dock

4. **主题/深色模式**
   - 当前：没有主题切换功能
   - 需要：根据系统设置自动切换、手动切换

### 6.3 🟢 低优先级

1. **性能优化**
   - 壁纸刷新优化（避免重复加载）
   - 运行中应用检测优化（避免频繁调用 Process API）
   - 图标缓存（避免重复加载 .ico 文件）

2. **动画效果**
   - Dock 应用悬停动画
   - 应用启动动画
   - 窗口切换动画

3. **国际化**
   - 当前：中英文混合
   - 需要：完整的本地化支持

## 7. 重要代码位置

| 功能 | 文件 | 方法/位置 |
|------|------|----------|
| 窗口初始化 | MainWindow.xaml.cs | `ConfigureDesktopWindow()` |
| 壁纸加载 | MainWindow.xaml.cs | `ReloadWallpaper()` |
| Dock 渲染 | MainWindow.xaml.cs | `ApplyDockConfiguration()` |
| 图标提取 | MainWindow.xaml.cs | `TryLoadApplicationIcon()` |
| 运行应用检测 | ApplicationCatalog.cs | `GetRunningApplications()` |
| 应用启动 | MainWindow.xaml.cs | `LaunchApplication()` |
| 设置窗口初始化 | SettingsWindow.xaml.cs | `ConfigureWindow()` |
| 配置保存 | LauncherSettings.cs | `Save()` |

## 8. 构建和运行

### 8.1 本地构建

```bash
cd WinUI3Desktop
dotnet restore
dotnet build -c Debug -f net10.0-windows10.0.19041.0
dotnet run
```

### 8.2 发布构建

```bash
dotnet publish -c Release -f net10.0-windows10.0.19041.0 -r win-x64 --self-contained
```

### 8.3 GitHub Actions

**触发方式：** 手动触发（workflow_dispatch）
**构建矩阵：** Debug/Release × x64/x64
**产物：** 自动上传构建输出

## 9. 已知问题

1. **Mica 背景在部分系统不生效**
   - 原因：`DWMWA_SYSTEMBACKDROP_TYPE` 仅在 Windows 11 22H2+ 支持
   - 临时方案：使用 XAML `AcrylicBrush` 作为回退

2. **应用图标无法从 .exe 提取**
   - 原因：缺少 `System.Drawing.Common` 包
   - 影响：大部分应用显示为彩色背景+首字母

3. **设置窗口白边**
   - 原因：DWM 非客户区渲染
   - 已修复：使用 `DwmSetWindowAttribute(DWMWA_NCRENDERING_POLICY, DWMNCRP_DISABLED)`

4. **右键菜单位置**
   - 在某些情况下，菜单可能显示在屏幕外
   - 需要额外边界检测

## 10. 下一步建议

### 10.1 立即执行

1. **添加 .exe 图标提取功能**
   ```bash
   dotnet add package System.Drawing.Common
   ```
   然后修改 `TryLoadApplicationIcon()` 使用 `Icon.ExtractAssociatedIcon()`

2. **完善应用启动**
   - 测试不同类型应用的启动方式
   - 添加 UWP 应用支持（使用 `IApplicationActivationManager`）

3. **完善设置页面**
   - 至少完成核心设置（Dock 设置、外观设置）

### 10.2 短期目标

1. 实现图标缓存系统
2. 实现多显示器支持
3. 完善应用抽屉（搜索、分页、拖拽）

### 10.3 长期目标

1. 插件系统
2. 社区主题/图标包
3. 性能优化和稳定性提升

## 11. 相关资源

- **Windows App SDK 文档：** https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/
- **WinUI 3 文档：** https://learn.microsoft.com/en-us/windows/apps/winui/
- **DWM API 文档：** https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/
- **GitHub 仓库：** https://github.com/MMCKB/Launcher

## 12. 安全注意事项

1. **Token 管理：** 本仓库曾使用 GitHub Personal Access Token 进行推送。请确保：
   - 不要将 Token 硬编码在代码中
   - 使用 GitHub Secrets 或环境变量管理敏感信息
   - 如果 Token 已泄露，立即在 GitHub 上撤销并重新生成

2. **应用权限：** 应用需要管理员权限才能：
   - 访问某些系统目录
   - 修改系统设置
   - 启动某些 UWP 应用

---

**文档版本：** v1.0  
**创建时间：** 2026-09-03  
**作者：** CatPaw AI Assistant
