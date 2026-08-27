# Launcher

一个使用 C#、.NET 10、WinUI 3 和 Windows App SDK 开发的第三方 Windows 桌面应用。

## 功能特性

- 全屏无边框窗口，覆盖任务栏
- 显示系统壁纸作为背景
- Acrylic 材质右键菜单，带有流畅动画
- 窗口和菜单圆角可自定义
- 菜单弹出/关闭时使用缩放+淡入淡出动画（120-180ms）
- 菜单项鼠标悬停高亮过渡效果（80-120ms）

## 系统要求

- Windows 10 版本 1809 或更高版本
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)
- [Windows App SDK 1.8+](https://aka.ms/windowsappsdk)

## 构建

```bash
dotnet restore
dotnet build -c Release
```

## 运行

```bash
dotnet run
```

## 云编译

本仓库包含 GitHub Actions 工作流，可在每次推送到主分支时自动构建。

## 许可证

本项目采用 MIT 许可证 - 详见 [LICENSE](LICENSE) 文件。
