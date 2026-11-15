# ProjectSync WPF UI 规范

界面使用 [lepoco/wpfui](https://github.com/lepoco/wpfui)。全局通过 ThemesDictionary 和 ControlsDictionary 加载主题，全窗口使用 FluentWindow、TitleBar 和 Mica。按钮、图标、文本输入、数据表、开关、菜单项和状态徽章使用库控件。

颜色使用库的动态资源。例如正文用 TextFillColorPrimaryBrush，说明用 TextFillColorSecondaryBrush，背景用 ApplicationBackgroundBrush。由 WPF UI 提供样式的标准 WPF 组合框、复选框、标签、滚动容器和进度条仍使用原有绑定及键盘行为。布局容器和 Windows 文件选择器沿用平台控件。

迁移控件时保留项目数据、同步范围、确认默认值、焦点与命令行为。发布 ZIP 需包含库 DLL、许可证与第三方声明。

固定宽度的整钮动作菜单和选值框采用左侧文字、右侧箭头，两者垂直居中。分段按钮的主操作文字在左段内居中，右段单独放置箭头与分隔线。保留 WPF UI 原生模板和交互。
