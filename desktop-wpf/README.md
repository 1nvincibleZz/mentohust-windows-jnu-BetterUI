# WPF 桌面客户端

`MainWindow.xaml` 与 `Theme.xaml` 定义首页、账号／参数页和控件样式。`Program.cs` 管理交互、页面动画、认证状态与托盘，`ProcessEngineClient.cs` 通过私有匿名管道连接独立 C++ 后台。前端不使用 MFC 界面；认证后台保留 MFC 兼容层。

## 配置与行为

正常运行使用 `%LOCALAPPDATA%\MentoHUST.Wpf\Config.ini`，首次没有示例账号。旧配置导入为工作副本，未知项、附加账号字段及无法解码的原密码保留。保存为 UTF-16 LE，保留 `Config.ini.previous`，外部修改冲突时拒绝覆盖。账号编辑只需账号与密码，已有 IP 保留，新账号默认 `0.0.0.0`。

选择已保存的有效账号与 pcap 网卡，后台和驱动就绪后才启用开始认证。旧客户端正在运行时拒绝新认证；退出 WPF 窗口结束本窗口的私有后台，不发送 EAP Logoff，断开按钮才调用原停止流程。

开机登录运行使用当前用户独立的 `MentoHUST.Wpf` Run 值，登记带引号的当前 EXE 绝对路径和 `--startup`。两页复选框同步，确定时写入，取消不写；启动项失败不保存配置，配置失败尝试恢复原启动项及类型，回滚异常明确报告。

“运行后自动认证”在下一次正常启动时生效，开机启动与手动打开客户端均支持。`StartupAuthentication.cs` 每个进程最多启动一次，等待后台握手和保存的 pcap 网卡链路就绪，最长 60 秒；不要求认证前已有 IP 或 Internet。自动路径调用与手动按钮相同的 `ToggleAuthenticationAsync`，沿用成功状态、热点及延迟收起行为。缺少保存账号／网卡、密码不可读、后台不可用、超时或启动失败只记录原因。手动操作、打开设置、更换选择、关闭窗口会取消等待；检测已有另一 WPF 客户端时不自动认证。预览与所有检查入口禁用真实自动认证。

认证成功后可等待 500 毫秒再收起到托盘；期间断开、状态改变或手动恢复会取消待执行的收起。手动托盘按钮立即执行。

热点选项默认关闭，保存在 `[WpfOptions] HotspotAfterSuccess`。首次认证成功后等待选定网卡联网，检查热点能力，使用有限查询、结束及等待权限核对 `8021x.exe`，在同一个句柄上结束一次并等待退出，再启动 Windows 移动热点。保留 `RJSuService` 与 MentoHUST 后台，SSID／密码沿用系统设置；自动重连、重复成功或动作失败不重复尝试。关闭和断开取消未完成流程，已经完成的系统动作保留。

## 单文件发布

`EmbeddedEngine.cs` 将嵌入的原后台释放到 `%LOCALAPPDATA%\MentoHUST.Wpf\Engine\<SHA256>\MentoHUST.Engine.exe`，先核对已有缓存，损坏时用临时文件原子替换。有效缓存复用，后台字节保持不变；首次准备在工作线程执行。主程序仍通过私有管道调用独立后台，认证核心无需迁移。后台工作目录保持为主程序目录，兼容相对数据包路径。开发构建未嵌入后台时使用同目录 EXE。

运行 `pwsh -File .\desktop-wpf\build-single-file.ps1 -VisualStudioRoot 'D:\MicrosoftVisualStudio'` 即可构建单文件版；也可用 `-EngineExecutable <已构建后台路径>`。输出 ZIP 只包含 `MentoHUST.BetterUI.exe` 和 `README.txt`，校验清单保留在 ZIP 外。使用者说明来自 `README-user.txt`。

可用 `pwsh -File .\desktop-wpf\tests\run-embedded-engine-checks.ps1 -OutputDirectory C:\Temp\mentohust-embedded -EngineExecutable <后台路径>` 验证释放、字节一致、复用、损坏修复及并发。单文件前端的 `--ui-engine-checks` 使用隔离缓存，不启动真实认证。

## 构建

```powershell
pwsh -File .\desktop-wpf\build-preview.ps1
pwsh -File .\desktop-wpf\build-engine.ps1 -VisualStudioRoot 'D:\MicrosoftVisualStudio'
.\desktop-wpf\bin\EngineTrial\MentoHUST.Desktop.Preview.exe
```

两个 EXE 位于同一输出目录。前端使用系统 .NET Framework 编译器，不需要 .NET SDK；后台需要 Visual Studio 的 C++／MFC x86 工具链、Windows SDK 和兼容 pcap 驱动。支持通过 `-OutputDirectory` 指定开发输出目录。

`.NET 8 Windows` 工程可通过 `-p:EngineExecutable=<后台绝对路径>` 嵌入已构建后台，可用 `dotnet build desktop-wpf/MentoHUST.Desktop.csproj` 构建，尚未在本机验证。

程序文件、窗口和托盘共用 `Assets/AppIcon.ico`。它包含九种尺寸，从校方 SVG 原稿生成，校徽外部透明。日常构建直接使用已提交的 ICO；重新生成可运行 `pwsh -File .\desktop-wpf\build-app-icon.ps1`。来源与处理方式见 [图标说明](Assets/AppIcon-source.md)。

## 检查

```powershell
.\desktop-wpf\bin\EngineTrial\MentoHUST.Desktop.Preview.exe --ui-checks C:\Temp\mentohust-ui
.\desktop-wpf\bin\EngineTrial\MentoHUST.Desktop.Preview.exe --ui-config-checks C:\Temp\mentohust-ui-config
.\desktop-wpf\bin\EngineTrial\MentoHUST.Desktop.Preview.exe --ui-engine-checks C:\Temp\mentohust-ui-engine
.\desktop-wpf\bin\EngineTrial\MentoHUST.Desktop.Preview.exe --ui-animation-checks C:\Temp\mentohust-ui-animation
.\desktop-wpf\bin\EngineTrial\MentoHUST.Desktop.Preview.exe --ui-auto-auth-checks C:\Temp\mentohust-ui-auto-auth
pwsh -File .\desktop-wpf\tests\run-auto-auth-checks.ps1 -OutputDirectory C:\Temp\mentohust-auto-auth
pwsh -File .\desktop-wpf\tests\run-configuration-checks.ps1 -OutputDirectory C:\Temp\mentohust-config -VisualStudioRoot 'D:\MicrosoftVisualStudio'
pwsh -File .\desktop-wpf\tests\run-engine-checks.ps1 -OutputDirectory C:\Temp\mentohust-engine
pwsh -File .\desktop-wpf\tests\run-startup-checks.ps1 -OutputDirectory C:\Temp\mentohust-startup
pwsh -File .\desktop-wpf\tests\run-hotspot-checks.ps1 -OutputDirectory C:\Temp\mentohust-hotspot
```

UI 检查使用内存或独立临时配置，导出虚构账号的截图，验证编辑、取消、保存、焦点、布局、动画、半秒收起和启动选项。配置兼容性检查使用 68 组虚构密码与原 C++ 函数双向对照，并用旧版 INI 接口读取保存结果。

自动认证检查使用模拟认证客户端与虚拟网卡探测，验证开关关闭、延迟就绪、只执行一次、重复请求、取消、超时、阻塞探测、无效配置与启动失败；界面检查覆盖保存账号／网卡、共享启动路径、打开设置取消等待及手动开始接管。没有真实 START、实际认证包、真实开机登记或热点动作。Windows 登录后的真实网卡时序仍待用户测试。

后台检查强制离线，验证配置、无效请求、START 拒绝、状态顺序、停止、正常和异常退出、Job 清理及 stdin EOF。UI 后台检查只连接、验证请求和空闲停止，不发送 START。

启动检查仅写 HKCU 私有 GUID 测试分支，不登记真实开机启动，结束后清理；覆盖路径引号、登记／取消、保存失败回滚、权限失败和其他值保留。热点检查使用模拟动作及独立受限子进程，核对一次执行、取消、结束权限与等待，不操作真实认证或热点。只读热点探测需要显式调用 `HotspotChecks.exe --probe <网卡 GUID>`。

连续 16 次页面往返检查完成后无残留动画变换、变换对象复用，静止时释放页面位图缓存。RenderingTime 间隔是本机观察，不能作为其他机器的帧率保证。自动检查不代替实际认证、DHCP、重连、系统登录启动及热点设备联网验收。

## 文件

- `SessionDraft.cs`、`LegacyConfigurationStore.cs`、`LegacyCodec.cs`：草稿与兼容配置。
- `AdapterCatalog.cs`、`EngineContract.cs`、`ProcessEngineClient.cs`：设备、认证接口和私有后台通信。
- `StartupRegistration.cs`：启动项登记、回滚和内存测试实现。
- `HotspotAfterAuthentication.cs`、`StartHotspot.ps1`：认证后单次热点流程与嵌入式辅助脚本。
- `native/EngineHost.cpp/.h`：原认证核心的隐藏窗口、线程与定时兼容层。
- `Assets/`：高清页眉和素材说明；正式校徽位于 `VS2012/MentoHUST/res/`。

[返回项目说明](../README.md)
