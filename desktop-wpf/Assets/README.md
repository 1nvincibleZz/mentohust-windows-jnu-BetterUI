# 界面素材来源

`HeaderHD.png` 使用内置 image_gen 根据用户提供的布局参考生成，实际尺寸为 2172 × 724；生成提示词见 [HeaderHD.prompt.md](HeaderHD.prompt.md)。标题、副标题及校训是静态图像，另提供读屏名称；右侧圆环与建筑是生成的背景装饰。

左侧正式校徽使用校方 PNG 原稿，素材及来源记录位于 [jnu_emblem_source.md](../../VS2012/MentoHUST/res/jnu_emblem_source.md)。运行时裁切校徽、保持图像比例并采用高质量缩放，原始 PNG 不改。

程序文件、窗口及托盘使用从校方 SVG 生成的透明多尺寸 `AppIcon.ico`，去掉外部白色方块，保留圆形校徽内部白色细节。矢量原稿、来源及生成方式见 [AppIcon-source.md](AppIcon-source.md)。

两种构建路径均嵌入页眉与校徽。其余字段、按钮、日志与设置页为原生 WPF 控件。
