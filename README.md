# MentoHUST Windows JNU BetterUI

为暨南大学锐捷 6.61 认证环境提供 WPF 桌面界面，基于 [Hjdd14/mentohust-windows-jnu](https://github.com/Hjdd14/mentohust-windows-jnu) 开发。通过独立 C++ 后台复用原认证核心，账号与参数继续兼容旧配置。

## 界面

| 首页 | 参数设置 |
| --- | --- |
| ![首页](docs/images/home.png) | ![参数设置](docs/images/settings.png) |

## 功能

- 单窗口首页与设置页，圆角控件、柔和图标、高清页眉和校徽；程序、窗口及托盘使用官网 SVG 生成的透明多尺寸图标。
- 平滑切页、滑动标签与复选框过渡；动画结束清理资源，保留键盘操作。
- 账号管理只填写账号和密码，网卡选择、认证状态及运行日志同步显示。
- 旧版 INI 导入、草稿取消、兼容保存、外部修改检测和上一份配置备份。
- 手动开始／断开认证；可选启动后自动认证，认证成功后可等待半秒收起到托盘。
- 开机自启动与开机自动认证：登录 Windows 后自动打开程序，等待网卡就绪后使用已保存的账号连接校园网。
- 可选认证后结束 `8021x.exe` 一次并启动 Windows 移动热点，问号提供说明。

## 下载与使用

从 [BetterUI 发布页面（Fork）](https://github.com/1nvincibleZz/mentohust-windows-jnu-BetterUI/releases) 下载 `MentoHUST-BetterUI-*.zip`，解压到固定目录，运行 `MentoHUST.BetterUI.exe`。单文件发布包只包含主程序和通俗操作说明 `README.txt`；认证后台已嵌入主程序，启动时自动准备，无需另开 EXE。GitHub 自动附带的 Source code 是源码，日常使用不需要下载。

1. 安装兼容 pcap 的抓包驱动；使用 Npcap 时启用 WinPcap API 兼容模式。
2. 第一次使用可点击“导入配置”选择旧客户端的 `Config.ini`，或在设置中添加账号。填写后点击“添加／更新”，再点击“确定”。
3. 选择有效账号和网卡，点击“开始认证”。
4. 需要开机自动认证时，先手动认证成功一次，再到参数设置中同时勾选“开机后自动运行”和“运行后自动认证”，点击“确定”。下次登录 Windows 时，程序会自动打开，等待网卡准备好后自动连接校园网。请把程序放在固定文件夹；移动程序后，重新保存一次开机运行设置。
5. 需要认证后开启热点时勾选对应选项并保存，执行结果显示在日志中。

配置位于 `%LOCALAPPDATA%\MentoHUST.Wpf\Config.ini`。导入后使用独立副本，原配置文件保留；发布包不包含账号或密码。关闭 WPF 窗口会退出其私有后台，点击“断开认证”才执行原认证核心的断开操作。

“运行后自动认证”生效于下一次正常启动（包括开机运行），每个进程只尝试一次。等待后台就绪和保存网卡的物理链路，最多等待 60 秒；认证前不要求已有 Internet 访问。缺少有效账号、网卡或后台时记录原因，超时与失败后不反复启动认证。打开设置、更换账号／网卡、手动认证或退出会取消待执行的自动认证；已有其他 WPF 客户端时跳过自动认证。离线预览与检查模式不会自动进行真实认证。

开机运行只登记当前用户的独立启动项，不修改其他程序的启动项。热点需要 Windows 接口、无线网卡、所选网卡的 Internet 连接和相应权限，名称／密码沿用系统设置；每次认证会话只尝试一次。

截至 2026-10-09，维护者已在自己的 Windows 11 电脑上实测确认：校园网认证、认证后热点、界面交互、开机自启动和开机自动认证均可正常使用。

当前验证环境使用 .NET Framework 和已有抓包驱动，后台需要 x86 VC／MFC 运行库。以上实测结果对应维护者的电脑及校园网环境，其他电脑仍需具备相应驱动和运行库。

## 构建

已有 Windows .NET Framework 4.x 编译器时，用 PowerShell 构建前端；后台需要 Visual Studio C++、MFC x86 工具链和 Windows SDK。将 `VisualStudioRoot` 改为本机安装目录：

```powershell
pwsh -File .\desktop-wpf\build-single-file.ps1 -VisualStudioRoot 'D:\MicrosoftVisualStudio'
.\desktop-wpf\bin\SingleFile\MentoHUST-BetterUI\MentoHUST.BetterUI.exe
```

单文件构建先生成后台再嵌入前端，输出双文件 ZIP（主程序及 README）。已有后台时可用 `-EngineExecutable` 指定它；嵌入过程不修改后台字节。开发时仍可分别运行 `build-preview.ps1` 与 `build-engine.ps1`。脚本的开发输出保留 `MentoHUST.Desktop.Preview.exe` 文件名，正常运行会连接认证后台；`--preview` 才是只在内存中操作的离线界面预览。发布包采用 `MentoHUST.BetterUI.exe` 文件名，两者使用同一前端。

另提供 .NET 8 Windows 工程：

```powershell
dotnet build .\desktop-wpf\MentoHUST.Desktop.csproj
```

当前验证的是 .NET Framework 构建路径，.NET 8 SDK 构建尚未在本机验证。

## 开发与验证

- [WPF 构建、功能和检查命令](desktop-wpf/README.md)
- [认证后台桥接与迁移边界](desktop-wpf/MIGRATION.md)
- [界面素材来源](desktop-wpf/Assets/README.md)
- [本版更新内容](CHANGELOG.md)

原 `VS2012/MentoHUST` 工程与认证核心保留，`references/` 是历史参考。原 Windows MentoHUST 来源见 [Google Code 存档](https://code.google.com/archive/p/mentohust/issues/51)。


## 致谢❤❤
感谢开源https://github.com/Hjdd14/mentohust-windows-jnu
