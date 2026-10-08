# 透明程序图标

- 原稿：[暨南大学官网 SVG](https://www.jnu.edu.cn/_upload/tpl/00/f5/245/template245/images/home/logo2.svg)，2026-10-08 下载，原文件保存为 `AppEmblem.svg`。
- `build-app-icon.ps1` 读取原稿第一个根分组中的校徽矢量路径，排除右侧校名。保留原始颜色和形状，在圆形校徽内部保留白色底色，圆形外部透明。
- `AppIcon.ico` 包含 16、20、24、32、40、48、64、128、256 像素共九种 32 位透明图标。矢量原稿直接按各尺寸渲染，外缘保留半像素以避免裁断。
- 程序文件图标、WPF 窗口图标和托盘图标均使用同一 ICO。页眉继续使用已有官方 PNG，认证核心保持不变。
- 需要重新生成时，在 Windows PowerShell 中运行 `./desktop-wpf/build-app-icon.ps1`；日常构建直接使用已生成的 ICO。
