# Launcher

A third-party Windows desktop replacement built with C#, .NET 10, WinUI 3, and Windows App SDK.

## Features

- Fullscreen borderless window that covers the taskbar
- Displays system wallpaper as background
- Acrylic material context menu with smooth animations
- Customizable corner radius for window and menu
- Scale + fade animations for menu open/close (120-180ms)
- Hover highlight transitions for menu items (80-120ms)

## Requirements

- Windows 10 version 1809 or later
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)
- [Windows App SDK 1.8+](https://aka.ms/windowsappsdk)

## Build

```bash
dotnet restore
dotnet build -c Release
```

## Run

```bash
dotnet run
```

## Cloud Build

This repository includes a GitHub Actions workflow for automatic builds on every push to the main branch.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
