# 暨南大学定制版 MentoHUST

这是一个面向暨南大学锐捷 6.61 认证环境修改的 Windows 版 MentoHUST。项目基于已有的 `mentohust-windows` 源码存档继续修改而来，原始来源见：

https://code.google.com/archive/p/mentohust/issues/51

本仓库当前主要维护 `VS2012/MentoHUST` 工程。根目录下的 `references/` 仅保留旧工程和备份代码作为参考。

## 主要改动

- 适配锐捷 6.61 的 EAPOL 认证流程：Start、Identity、MD5 Challenge、Success。
- 默认启用 `Ruijie6Mode=1`，认证成功后不再发送旧版 `0x888e 01bf` 心跳包。
- 恢复账号设置和参数设置页面，支持添加账号、密码、IP 和选择网卡。
- “开机后自动运行”改为写入当前用户 `HKCU` 启动项，不需要管理员权限写入 `HKLM`。

## 使用方法

1. 安装 Npcap 或 WinPcap。使用 Npcap 时建议启用 WinPcap 兼容模式。
2. 从 `dist/` 中取出发布包，或自行构建 `VS2012/MentoHUST.sln` 的 `Release_GB|Win32` 配置。
3. 运行 `MentoHUST.exe`。
4. 点击“设置”，在“账号设置”中添加校园网账号、密码和本机 IP。
5. 在“参数设置”中建议先使用：
   - 组播地址：私有
   - DHCP 方式：不使用
   - 认证超时：8 到 10 秒
   - 心跳间隔：任意，锐捷 6.61 模式下不会发送旧心跳
   - 重连间隔：0
   - 自定义认证数据包：不勾选
   - 绑定网关 MAC：不勾选
6. 回到主界面，选择刚添加的账号和正确网卡，然后点击“认证”。

如果认证失败，请抓取新包并重点查看 `Request Identity`、`Request MD5`、`Failure/Success` 包。

## 构建方法

推荐使用 Visual Studio 或可用的 MSBuild 环境构建。可以先打开 Visual Studio 开发者 PowerShell，或在 PowerShell 7 中把 `<MSBuild.exe>` 替换为本机 MSBuild 的完整路径：

```powershell
pwsh -NoProfile -Command "& '<MSBuild.exe>' 'VS2012\MentoHUST.sln' /t:Rebuild /p:Configuration=Release_GB /p:Platform=Win32 /m /v:minimal /nologo"
```

构建输出位于：

```text
VS2012/Release/MentoHUST.exe
```

静态回归测试：

```powershell
python -m pytest tests/verify_ruijie6_static.py -q
```

## 说明

本项目只针对当前抓包环境优先适配，不保证适用于所有学校或所有锐捷 6.x 定制版本。旧版源码和备份工程仅作为参考保留。
