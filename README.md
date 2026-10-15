# PegLine 挂绳

PegLine 是一款 Windows 桌面小工具。新的截图，以及你开启后的剪贴板图片，会像衣服一样挂在屏幕顶部晾衣绳上。点一下就能复制，拖出去就能丢进资源管理器或其他应用。

图片、设置和错误日志只写在本机，没有网络请求，没有账号，也没有遥测。

## 下载安装（推荐）

1. 打开本仓库的 [Releases](../../releases) 页面。
2. 下载最新的 **`PegLine-Setup-x.y.z.exe`**。
3. 双击运行安装向导（需要管理员权限，默认装到 `C:\Program Files\PegLine`）。
4. 可按需勾选「创建桌面快捷方式」；开始菜单会自动添加 PegLine。
5. 安装完成后可直接运行。

**运行时说明：** 安装包为 **自包含（self-contained）** 发布，已内嵌 .NET 8 与 Windows App SDK，**不必再单独安装 Desktop Runtime 或 Windows App Runtime**。体积较大（约数百 MB）属正常现象。

当前安装包面向 **64 位 Windows（win-x64）**。Windows 10 版本 2004（10.0.19041）及以上，或 Windows 11。

卸载：在「应用和功能」中卸载 PegLine，或开始菜单中的卸载项。  
`%LOCALAPPDATA%\PegLine` 下的设置与 Saved 图片**默认保留**，方便重装后继续用。

## 本机数据位置

| 路径 | 内容 |
| --- | --- |
| `%LOCALAPPDATA%\PegLine\settings.json` | 设置 |
| `%LOCALAPPDATA%\PegLine\Saved\` | 「存到 Saved」的图片（设置里可改成其他文件夹） |
| `%LOCALAPPDATA%\PegLine\clipboard\` | 从剪贴板落下的 PNG 缓存 |
| `%LOCALAPPDATA%\PegLine\error.log` | 仅异常时追加 |
| `%LOCALAPPDATA%\PegLine\instance.lock` | 单实例锁 |

默认还会监视 `%USERPROFILE%\Pictures\Screenshots`（Win+PrtScn 的保存位置）。

## 开发者：从源码构建

### 环境

- Windows 10 2004+ / Windows 11
- [Visual Studio 2022](https://visualstudio.microsoft.com/vs/) 17.10+，工作负载：
  - .NET 桌面开发
  - 使用 C# 的 Windows 应用开发（Windows App SDK）
- .NET 8 SDK（8.0.402+）

本仓库是 **非打包（unpackaged）** WinUI 3 应用（`WindowsPackageType=None`）。

### 调试运行

```powershell
dotnet build PegLine.sln -c Debug -p:Platform=x64
dotnet run --project PegLine\PegLine.csproj -c Debug -p:Platform=x64
```

或用 Visual Studio 打开 `PegLine.sln`，平台选 **x64**，启动配置 `PegLine (Unpackaged)`，按 F5。

核心库测试：

```powershell
dotnet test PegLine.Core.Tests\PegLine.Core.Tests.csproj
```

### 打安装包（Setup.exe）

已提供一键脚本（自包含 publish + [Inno Setup](https://jrsoftware.org/isinfo.php) 6）：

```powershell
# 需已安装 .NET 8 SDK；首次打包需安装 Inno Setup 6
powershell -ExecutionPolicy Bypass -File scripts\publish-installer.ps1
```

产物：

- `artifacts\publish\win-x64\` — 自包含应用目录
- `dist\PegLine-Setup-1.0.1.exe` — 安装包

可选参数：`-Version 1.0.1`、`-SkipPublish`、`-SkipInstaller`。

等价的手动发布命令：

```powershell
dotnet publish PegLine\PegLine.csproj -c Release -r win-x64 --self-contained true `
  -p:Platform=x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true `
  -o artifacts\publish\win-x64
```

然后用 Inno Setup 编译 `installer\PegLine.iss`。

> 说明：日常 Debug 不会强制自包含，以免拖慢 F5。自包含与 Windows App SDK 内嵌仅在 Release 发布 / 打包脚本中启用。

## 怎么用

绳子默认在指针所在显示器工作区的顶部。空白条上的点击会穿透到后面的窗口。

| 操作 | 结果 |
| --- | --- |
| 单击 peg | 把图片复制到剪贴板（位图 + 文件） |
| 拖到资源管理器 | 移动文件；按住 Ctrl 再放下为复制 |
| 拖到其他应用 | 对方收到一份副本 |
| peg 右上角 × | 从绳子取下（是否删原文件看设置） |
| 托盘左键 / Ctrl+Alt+T | 显示或隐藏绳子 |
| 托盘右键 | 显示、隐藏、设置、退出 |

## 项目结构

```
PegLine.sln
PegLine.Core/          设置、peg 列表、文件夹监视
PegLine.Core.Tests/
PegLine/               WinUI 3 非打包应用
installer/             Inno Setup 脚本与简体中文语言文件
scripts/               publish-installer.ps1
artifacts/              publish 输出（gitignore）
dist/                  Setup.exe 输出（gitignore）
```

目标框架：`net8.0-windows10.0.19041.0`。NuGet：`Microsoft.WindowsAppSDK` 2.5.1。

## 隐私

PegLine 不发起网络连接，不读取账号，不上传截图。状态只写在 `%LOCALAPPDATA%\PegLine`。点击 peg 只会把图放进本机剪贴板。

## 许可

见仓库内许可文件（若有）。发布到 GitHub 时，请将 `dist\PegLine-Setup-*.exe` 上传到 Release 附件供用户下载。
