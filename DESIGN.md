# SiteLift WPF UI 规范

界面基于 [lepoco/wpfui](https://github.com/lepoco/wpfui)（Fluent Design / WinUI 3 风格）。全局通过 `ThemesDictionary` 与 `ControlsDictionary` 加载浅色/深色主题，全部窗口统一使用 `FluentWindow`、`TitleBar`、`WindowCornerPreference="Round"`、`WindowBackdropType="Mica"` 与 `SiteLift.ico`，并开启 `UseLayoutRounding="True"`。

---

## 1. 颜色、字体与表面层级

- **动态主题资源与零元素级不透明度**：严禁硬编码明暗色值，严禁对文本元素使用 `UIElement.Opacity < 1`（防止离屏光栅化导致 ClearType 退化；需要介于主色与次色之间的高可读灰阶时，一律使用带 `Opacity` 的 `SolidColorBrush Color="{DynamicResource TextFillColorPrimary}"` 画刷级透明以完整保留 ClearType）。主文本用 `TextFillColorPrimaryBrush`，表格次要列/表头/未选中页签/中性徽章用 `76%–80%` 画刷级主色，次要说明用 `TextFillColorSecondaryBrush`，弱提示/版本号/空状态副文案用 `TextFillColorTertiaryBrush`，窗口底板用 `SolidBackgroundFillColorBaseBrush`，卡片表面用 `CardBackgroundFillColorDefaultBrush`，卡片描边用 `CardStrokeColorDefaultBrush`，分隔线用 `DividerStrokeColorDefaultBrush`，警示/危险语义用 `SystemFillColorCriticalBrush` / `SystemFillColorCautionBrush` / `SystemFillColorSuccessBrush`。
- **字体与统一字阶（Windows 11 WinUI 3 Type Ramp + 中文适配）**：
  - 全局在 `App` 静态构造中统一覆盖 `FrameworkElement.LanguageProperty` 为 `zh-CN`，确保中文标点度量与 CJK 回退链一致；所有窗口根级（`MainWindow`、`FileCopyWindow`、`WslManagerWindow`、`FluentDialog`）统一设定 `FontSize="14"`、`FontFamily="{StaticResource WorkspaceTypeface}"` 与 `ClearType + Ideal + Fixed` 渲染基线。
  - **四套字体栈与统一 `Ideal` 渲染**：小字说明/徽章/紧凑控件采用 `WorkspaceSmallTypeface`（`Segoe UI Variable Small, Segoe UI, Microsoft YaHei UI`）；正文/卡片标题/按钮/列表/全部路径与规则表单框（含多行路径框 `WorkspaceMultilineInput`）统一采用 `WorkspaceTypeface`（`Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI`）；页级主标题采用 `WorkspaceDisplayTypeface`（`Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI`）；仅真正的代码、Shell 维护脚本、JSON 编辑器与运行日志采用 `WorkspaceMonoTypeface`（`Consolas, Cascadia Mono, Microsoft YaHei UI`，与 Windows 版 VS Code 默认纤细等宽一致）。全站比例与等宽文本统一采用 **`ClearType + Ideal`** 矢量亚像素渲染，并配固定行高（`BlockLineHeight`）。
  - **严控整数 Type Ramp（严格 3 档比例字阶 + 2 档等宽字阶，仅用 `Normal` / `SemiBold` 避免雅黑缺 `Medium` 导致中英粗细割裂）**：
    - 页级主标题（`SettingsPageTitle`，含设置页与 WSL 管理页）：`20px / 28px` `SemiBold` `WorkspaceDisplayTypeface`（对齐 WPF-UI `Subtitle`）；
    - 分组标题（`SettingsGroupHeader`）/ 面包屑当前项目 / 弹窗强调标题：`14px / 20px` `SemiBold` `WorkspaceTypeface`；
    - 核心交互与表单内容（工具栏按钮、表头 `WorkspaceColumnHeader`、结果页签 `HomeResultsTabItem`、表格数据行、设置侧边栏、卡片主标题 `SettingsCardTitle`、表单标签 `SettingsFieldLabel`、单行及多行路径/规则输入框 `WorkspaceMultilineInput`）：统一 `14px` `Normal` `WorkspaceTypeface`（单行/常规行高 `20px`，多行路径框行高 `22px`）；表头常态与悬停保持一致的 `TextFillColorPrimaryBrush`（仅悬停呈现浅灰底块，不做文字变色），结果区未选中页签采用画刷级 `76%` 主色（悬停/选中切 `TextFillColorPrimaryBrush`）；
    - 页副标题（`SettingsPageSubtitle`，`12px/18px`）/ 卡片副标题（`SettingsCardSubtitle`，`12px/17px`）/ 徽章/底栏链接/紧凑按钮/磁盘数值：`12px Normal` `WorkspaceSmallTypeface`；
    - 只读日志（`WorkspaceLog` / `WorkspaceRichLog`）：`12px / 19px` `WorkspaceMonoTypeface`；JSON 编辑器与 Shell 维护脚本框（`WorkspaceCodeEditor` / `WorkspaceMonoInput`）：`13px / 20px` `WorkspaceMonoTypeface`。
  - **等宽数字（Tabular Figures）**：全应用数值徽章、版本号、磁盘容量、进度计数与状态统计统一开启 `Typography.NumeralAlignment="Tabular"`，杜绝数字列错位与刷新抖动。
  - **C# 动态弹窗与控件**一律通过 `SetResourceReference` 复用 `WorkspaceVisuals.xaml` 统一样式，禁止在 C# 中硬编码字体栈或散落字号。
- **三层明度阶梯**：
  1. 整窗背景透出 Windows 11 原生 `Mica` 云母材质（主内容容器 `Background="Transparent"`，无支持时自动回退至 `ApplicationBackgroundBrush`），标题栏与内容区浑然一体；
  2. 首页顶部命令栏直接浮于 Mica 底板之上，主要内容区块、数据表与表单卡片使用 `SettingsCard`（`CornerRadius="6"`、`1px CardStrokeColorDefaultBrush`）或库控件 `ui:CardControl` / `ui:CardExpander`，静止卡片不加外阴影（阴影仅用于下拉菜单与弹窗浮层）；
  3. 避免“框套框”：结果区 `TabControl` 内的数据表、日志区以及「快速配置」JSON 编辑区均采用顶部 `1px` 细分隔线衔接无边框内容区，不再套叠重边框面板。
- **4px 栅格**：窗口外边距 `24 DIP`，卡片间距 `8–12 DIP`，卡片内边距 `16,14`，控件组间距 `8 DIP`。

---

## 2. 「图标 + 中文」按钮基线规范（已实测验收）

WPF UI 默认按钮模板按英文 `Segoe UI` 设计（`ButtonPadding="11,5,11,6"`、`ButtonIconMargin="0,0,8,0"`），在中文 `Microsoft YaHei UI`（顶部行盒留白偏大、无下行部首）叠加 `FluentSystemIcons`（字形自带 `~1.5 DIP` 左右内缩）时，必须通过项目公共样式统一做光学对齐，**禁止在单个按钮上散落硬编码 `Padding` 或 `X` 偏移补丁**。

### 统一样式基线（定义于 `WorkspaceVisuals.xaml`）

| 样式名 | 适用场景 | 关键参数 |
| :--- | :--- | :--- |
| `WorkspaceActionIcon` | 所有「图标 + 文字」按钮内图标 | 严格使用 Fluent Regular **`*16` 族**；`Width="16"`、`Height="16"`、`FontSize="16"`、水平垂直居中、`Margin="0,0,-3,0"`、`TranslateTransform Y="1.5"` |
| `WorkspaceMenuIcon` | 下拉菜单与右键菜单项内图标 | 严格使用 Fluent Regular **`*16` 族**；`Width="16"`、`Height="16"`、`FontSize="16"`、水平垂直居中、`TranslateTransform Y="1.25"` |
| `WorkspaceTextButton` | 标准纯文字操作按钮（高 `34 DIP`） | `MinHeight="34"`、`Padding="12,5.5,12,6.5"`、水平垂直居中，保证中文基线与图标按钮对齐 |
| `WorkspaceIconButton` | 标准操作按钮（高 `34 DIP`） | `MinHeight="34"`、`Padding="11,5.5,12.5,6.5"`、`HorizontalContentAlignment="Center"`、`VerticalContentAlignment="Center"` |
| `WorkspaceCompactActionIcon` | 紧凑图标按钮内图标 | `Width="14"`、`Height="14"`、`FontSize="14"`、`Margin="0,0,-2,0"`、`TranslateTransform Y="1"` |
| `WorkspaceCompactIconButton` | 紧凑操作按钮（高 `30 DIP`） | `MinHeight="30"`、`WorkspaceSmallTypeface` `12px`、`Padding="9,3.5,10.5,4.5"` |
| `WorkspaceCompactDropDownButton` | 紧凑下拉按钮（高 `30 DIP`） | `MinHeight="30"`、`WorkspaceSmallTypeface` `12px`、`Padding="6,0"`，与紧凑图标按钮同字号对齐 |

### 光学指标与验收基线（192 DPI / 200% 缩放实测）

- **同一水平中线**：图标中心与中文文字中心垂直偏差 `ΔY ≤ 0.25 DIP`（视觉完全重合）。
- **上下边距对称**：按完整 `34 DIP` 外框（含 Primary 按钮底部 `1 DIP` 立体描边）计，图标上下约 `10.0 / 10.0 DIP`，中文上下约 `10.5 / 10.0 DIP`。
- **左右对称与图文分组**：左视觉边距 `13–14 DIP`、图标↔文字间隔 `6.5–8.5 DIP`、右视觉边距 `13.5–14 DIP`（图文间隔约为外边距的一半，紧凑成组）。
- **图标选型与克制**：
  - 按钮、Tab 页头、菜单项内统一用 **`*16` 族**（若个别符号在 `Wpf.Ui` 中码点 `> U+FFFF` 如 `PlugConnected16`，因 `SymbolIcon` 16 位字符转换会截断，须改用同族 BMP 码点 `≤ U+FFFF` 的变体如 `PlugConnected20` / `WindowConsole20`）；卡片头、侧栏导航与空状态统一用 **`*24` 族**，不混用线宽。
  - 自带下拉箭头的 `SplitButton` / `DropDownButton`（如首页「新建项目」「工具」）不额外叠加左侧图标，避免「图标 + 文字 + 箭头」三段失衡。
  - 纯文字按钮与图标按钮并排时（如底栏「检查更新」与「导出诊断」），采用相同的垂直内边距（`5.5 / 6.5`）保持文字基线一致。

---

## 3. 首页与主操作层级

- **单主操作原则**：同一视图最多突出 1 个 `Primary` 强调色按钮。有可执行的批处理时，按「重试推送 → 提交并推送 → 应用同步 → 构建」的顺序选出主操作；其余情况突出「新建项目」。
- **悬浮命令栏（Windows 11 Files CommandBar）**：顶部批处理按钮（「构建」「预览」「同步」「推送」，按需显示「重试」，分别使用 `Box16`、`Window16`、`ArrowSync16`、`ArrowUpload16`、`ArrowClockwise16`）与右侧操作（「新建项目 / 导入 JSON」「工具」及竖线隔开的主题切换图标菜单）收纳于统一悬浮 `SettingsCard` 命令栏（`Padding="10,6"`，`Margin="0,0,0,12"`）；非主操作按钮采用无边框 `Transparent` 轻样式，并显式将无边框按钮的 `ButtonBackgroundDisabled` / `ButtonBorderBrushDisabled` 置为 `Transparent`、沿用 WinUI 3 原生 `ButtonForegroundDisabled`，确保浅色与深色主题禁用态均无灰底框残留且对比度一致。
- **菜单与弹出层**：「工具」与行尾菜单默认右边缘对齐并保持与触发按钮 **`4 DIP`** 呼吸间距（贴近底边时向上展开同样留 `4 DIP`），全局 `ContextMenu` 最小宽度 `156 DIP`，菜单项图标统一使用 `WorkspaceMenuIcon`（`*16` 族 + `Y="1.25"` 光学居中）；顶部「主题」采用无下拉框的一键循环切换图标按钮（浅色 `☀` → 深色 `☾` → 跟随系统 `◐`，三态各配独立 `16×16` 矢量图标与动态 `ToolTip`）；组合框与菜单沿用 WPF 原生弹出动画。
- **双卡独立布局、可收起结果抽屉与极简状态栏**：批量运行进度与浏览器预览状态分别使用带图标的 `SettingsCard` 独立展示并保留停止入口（批量运行卡片内使用 `18×18` `ui:ProgressRing` 指示进行中状态）；上方项目列表与下方结果区保持两张独立 `SettingsCard`，中间以 `16px` 呼吸间距 + 居中胶囊 `GridSplitter` 自由调节上下高度；下方结果卡 `38px` 顶栏与上方 `38px` 表头同高同底色（`ControlAltFillColorSecondaryBrush`），左侧采用无框 WinUI 3 `SelectorBar` 页签（`变更清单 / 运行日志`，`14px Normal` 画刷级 `76%` 主色常态 / `TextFillColorPrimaryBrush` 悬停与选中态 + `14px` 选中态主题蓝图标 + 底部 `2.5px` 主题蓝胶囊条），右侧自适应承载上下文工具——「变更清单」展示绿/蓝/红语义计数胶囊（`+新增 / ~修改 / -删除`），「运行日志」直接内嵌 `Copy16` / `Delete16` 图标按钮省去多余二级栏，最右提供一键收起/展开抽屉按钮（收起态折为 `40px` 紧凑条并保持与上卡片 `12px` 呼吸间距，点击页签或开始构建自动展开）；底栏瘦身为 `24px` 记事本风极简透明状态条，左侧展示项目与勾选汇总，右侧仅放无边框轻文字入口「导出诊断 | 检查更新 vX.Y.Z」。

---

## 4. 列表、表格与复选框规范

- **表头与排版权重**：项目表顶栏与结果卡页签顶栏共用 `38px` 高 `ControlAltFillColorSecondaryBrush` 单层通栏底带，表头与结果页签统一采用 `14px Normal` `WorkspaceTypeface`（与工具栏、数据行、设置侧栏和卡片标题一致），其中表头文字始终保持 `TextFillColorPrimaryBrush`（悬停仅显示浅灰底块、无文字变黑跳变），结果页签未选中态为画刷级 `76%` 主色、悬停/选中切换 `TextFillColorPrimaryBrush`（零元素级 `Opacity`）；数据行采用 `42px` 单行精炼布局，项目名称列采用 `18×18` 无外框矢量架构图标（自动识别 VuePress、VitePress、Hexo、Hugo、Astro、Docusaurus、MkDocs、Quartz、Rspress、Nuxt、Next.js、Jekyll、mdBook、Docsify，未识别回退至 `17px` 主题色 `Folder20`）配 `14px Normal` 主色，`Git 本地仓库` 与 `Git 远端仓库` 分列展示（`14px Normal` + 画刷级 `80%` 主色）；「快捷访问」列 7 枚图标全部统一至同网格 `*20` 族（`FontSize="17"`）并以 `1×14` 细分隔线将 4 项浏览入口与 3 项维护入口（重装依赖、升级主题、扫描站点画像）分组，悬停时不加矩形外框，仅点亮图标为 `AccentTextFillColorPrimaryBrush` 并呈现双层聚焦蓝荧光（背后叠加 `VisualBrush` 图标轮廓蓝荧光 + 极轻中心径向柔光，明暗主题自适应且矢量图标本体保持锐利）；变更清单路径列采用「目录前缀次色 + 文件名主色」分段高亮。
- **运行日志与 JSON 编辑器**：首页运行日志采用无边框只读 `RichTextBox`（`12px` 等宽字体、`19px` 固定行高，`Padding="16,12"`，直接贴合结果卡表面，普通输出行退让至 `TextFillColorSecondaryBrush`，标题/成功/警告/错误行保留高对比度语义着色，支持划选复制与居中空状态引导）；「快速配置」JSON 编辑区采用零第三方依赖的自研 `JsonCodeEditor`（`WorkspaceCodeEditor`，`13px / 20px` `Consolas` 优先 `Ideal + ClearType`，全 `Normal(400)` 不加粗，直接铺满卡片次级底板，提供 100% VS Code Light Modern / Dark Modern 官方色板语法着色、3 级循环括号彩标、无竖线同底色行号槽、垂直缩进参考线与当前作用域活动缩进线高亮、当前行柔和底色高亮、Tab 插入 2 空格，聚焦时底部显示 WinUI 3 风格的 `2px` 强调色指示条）；Shell 维护脚本输入框套用 `WorkspaceMonoInput`（`13px / 20px` 等宽 + `ClearType + Ideal`），多行同步路径与排除规则表单框套用 `WorkspaceMultilineInput`（`14px / 22px` 比例字体，与单行路径框完全统一）。
- **全选与行内复选框**：表头全选复选框使用专用零内边距居中模板 `WorkspaceCheckColumnHeader`（列宽 `64 DIP`，透明底色贴合卡片顶栏通带），表头与行内复选框统一使用 `WorkspaceTableCheckBox`（视觉框体 `18×18` 与架构图标等高、`28×28` 舒适点击热区、`CornerRadius="4"`，未勾选常态采用柔和 `26%` 主色细描边、悬停升至 `46%`，勾选与半选态填充 `AccentFillColorDefaultBrush` 主题蓝配白色矢量勾选/短横）。
- **状态徽章与对齐**：全应用 `ui:Badge` 在 `App.xaml` 中统一采用 `12px Normal`、`WorkspaceSmallTypeface`、`Tabular` 等宽数字与 WinUI 3 柔和语义配色——中性态（`Secondary`）采用 `8% TextFillColorPrimary` 柔和胶囊底色 + 画刷级 `78% TextFillColorPrimary` 清晰文字，语义态（`Success` / `Info` / `Caution` / `Danger`）统一通过 `Appearance` 触发对应 `SystemFillColor*BackgroundBrush` + 语义主色，确保浅色/深色与勾选行底色下均轮廓清晰。快捷操作列居中，磁盘数值右对齐。
- **行操作与空状态**：首页项目行支持双击与右键「编辑配置」「移除项目」；全应用空状态统一复用 `WorkspaceEmptyIcon`（`24px TextFillColorTertiaryBrush`）、`WorkspaceEmptyTitle`（`14px/20px TextFillColorSecondaryBrush`）与 `WorkspaceEmptySubtitle`（`12px/17px WorkspaceSmallTypeface TextFillColorTertiaryBrush`），避免大面积空白框。

---

## 5. 项目配置页规范

- **顶栏上下文与状态（WinUI 3 面包屑顶栏）**：配置页顶栏取消老式实心白底通栏与硬底线，直接铺设于通透 Mica 底板；左侧采用单一返回入口的纯 WinUI 3 面包屑（无边框可点「项目列表」→ 矢量 `›` → `18×18` 架构图标与 `14px SemiBold` 当前项目名 `ActiveProjectHeaderTitle` 及动态「未保存」「JSON 待应用」语义徽章），右侧统一放置带图标操作组——轻样式无边框「导出 JSON」（`ArrowUpload16`）紧邻主操作「保存项目」（`Save16`，无改动时保持与首页一致的 `50%` 可读禁用态，有改动时切换 `Primary` 主题蓝）；单行长路径、URL 与命令输入框开启 `ClearButtonEnabled="True"`，便于快速清空重写。
- **侧栏导航与圆角内容画布**：左侧 `208 DIP` 导航栏取消灰底与硬竖线，直接浮于 Mica 材质（导航项最小高 `40 DIP`、间距 `4 DIP`、圆角 `5px` 柔和选中底板、左侧 `3×16` 主题强调条与统一 `*24` 图标，共 4 项：「项目与 Git 仓库」「本地构建环境」「同步与清理」「快速配置」）；右侧内容区采用 Windows 11「设置」与 `lepoco/wpfui` `NavigationView` 标志性的左上 `8px` 圆角半透明内容画布（`LayerFillColorDefaultBrush` + 上/左 `1px` `CardStrokeColorDefaultBrush` 描边），3 个表单页统一采用 `ui:DynamicScrollViewer` 悬浮细条滚动条。
- **单页内聚与表单布局（Windows 11 设置垂直基准线）**：
  - 所有配置页统一最大内容宽度 `900 DIP`（`Padding="32,20,32,24"`，卡片净宽 `836 DIP`）并在右侧内容区水平居中（`HorizontalAlignment="Center"` / 全高页 `MaxWidth="900"` + `Stretch` 居中，默认窗口与宽屏全屏时左右留白均衡、窄屏时自动贴合容器宽度，滚动条保持在窗口最右侧）；顶部使用 `SettingsPageTitle` + `SettingsPageSubtitle`，页内通过 `SettingsGroupHeader`、`SettingsSectionCard`（`SettingsCardHeader` + `SettingsCardHeaderIcon` + `SettingsCardBody`）、`ui:CardControl`（`IsTabStop="False"` 跳过非交互卡片焦点）与 `SettingsCardExpander`（`ui:CardExpander`）组织层次。
  - **统一 `52 DIP` 文本左基准线**：与 `lepoco/wpfui` `CardControl` / `CardExpander`（`PaddingLeft="14"` + `24px` 图标 + `14px` 间距 = `52 DIP`，含 `1px` 外框实测 `X = 53.00 DIP`）及 Windows 11 设置页 `SettingsExpander` 规范一致；所有带图标的配置卡片标题、`SettingsCardBody` 表单主体（`Padding="52,14,14,14"`）与 `SettingsCardExpander` 展开体（`ContentPadding="52,14,14,16"`）严格对齐至同一条 `52 DIP` 垂直基准线，并以贯穿卡片全宽的 `1px` 分隔线和次级卡片底色（`CardBackgroundFillColorSecondaryBrush`）区分标题区与配置区；「快速配置」的独立全幅 JSON 代码编辑区在工具栏分隔线下方保留全宽（`14 DIP` 左右边距）以保障代码可读性。
  - **「项目与 Git 仓库」** 将项目名称、Windows 本地目录、远端地址/名称/分支与本地初始化/维护整合为单一连贯流程：异步探测本地仓库时显示 `16×16` `ui:ProgressRing`，实时展示本地目录状态徽章，自动检测已有 Git 仓库的分支与远端地址（打开已有项目时保持只读，不误触发未保存状态），缺项校验原地提示并聚焦对应输入框，不跨页跳转；已连接 Git 的目录将高风险「重置 Git 数据」收进默认折叠的维护卡片。
  - **「本地构建环境」** 将发行版、用户与 Node.js 版本并排对齐，额外 PATH 与可选维护命令收入 `CardExpander`（有配置时自动展开），构建后预览使用标准 `CardControl` + `ToggleSwitch`。
  - **「同步与清理」** 以实时计数徽章汇总同步路径与双栏排除规则；开启永久清理未跟踪文件时动态展开警示 `InfoBar`。
  - **「快速配置」** 在卡片内提供实时同步/语法状态徽章、复制、导入、危险语义清空与仅在有改动时启用的「应用到表单」，分隔线下方无缝衔接无边框全幅代码画布（`WorkspaceCodeEditor`，左侧 `48 DIP` 行号槽 + `Padding="0,12,14,12"`，不缩进至 `52 DIP`）。

---

## 6. 工具窗口与交互原则

- **功能连贯与前置校验**：UI 变更前先核对任务流、作用对象与前置条件；反馈 `InfoBar` 贴近触发操作的卡片；危险操作（注销删除、重置 Git、清空配置、永久清理）统一使用 `Danger` / 警示色并要求明确确认；耗时的异步预览、复制、仓库探测与 WSL 操作均配备 `ui:ProgressRing` 反馈。
- **文件复制**：分为「路径与选项」「复制清单」两张卡片，源/目标路径框启用 `ClearButtonEnabled="True"` 并随 Windows / WSL 位置切换动态更新 `PlaceholderText`；先预览再复制，仅在两端均为现有目录时允许交换路径；操作列采用语义 `ui:Badge`。
- **WSL 环境管理**：主窗口以徽章展示 WSL 版本与实例数、菜单项配齐图标、日志区使用 `CardExpander`；「配置开发环境」等子对话框采用统一卡片头、工具官方图标单行组件卡片与分隔线底栏。
- **发布打包**：迁移或新增控件时保留原有配置兼容、安全审查、焦点与键盘导航行为；发布 ZIP 需包含库 DLL、许可证与第三方声明。
