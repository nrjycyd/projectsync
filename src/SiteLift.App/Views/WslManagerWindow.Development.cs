using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using SiteLift.Core;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace SiteLift.App.Views;

public partial class WslManagerWindow
{
    private static readonly HttpClient VersionClient = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly ConcurrentDictionary<string, (DateTimeOffset LoadedAt, string[] Versions)> RemoteVersionCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan RemoteVersionCacheTtl = TimeSpan.FromMinutes(10);

    public sealed class PresetChoice
    {
        public required WslEnvironmentPreset Preset { get; init; }
        public required string Label { get; init; }
        public required PresetActivation Activation { get; init; }
        public bool IsActive => Activation == PresetActivation.Active;
        public bool IsPartial => Activation == PresetActivation.Partial;
        public string StatusTag => Activation switch
        {
            PresetActivation.Active => "已激活",
            PresetActivation.Partial => "部分就绪",
            _ => ""
        };
    }

    private sealed class ToolCardSlot
    {
        public required WslToolGroup Group { get; init; }
        public required FrameworkElement Root { get; init; }
        public required ToggleSwitch Switch { get; init; }
        public required ComboBox VersionBox { get; init; }
        public required ContentControl StatusBadgeHost { get; init; }
        public required Badge SubSummaryBadge { get; init; }
        public CardExpander? Expander { get; init; }
    }

    private void ConfigureDevelopmentClick(object sender, RoutedEventArgs e)
    {
        if (busy || Row(sender) is not { } instance) return;

        bool darkTheme = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() == Wpf.Ui.Appearance.ApplicationTheme.Dark;
        var linkedProjects = projectsUsing(instance.Name).ToArray();
        var linkedFrameworks = linkedProjects.Select(p => p.Framework).Where(f => f != SiteFramework.Generic).Distinct().ToArray();
        var linkedGroups = WslDevelopmentEnvironment.LinkedFrameworkGroups(linkedFrameworks);

        WslToolProbeResult currentProbe = instance.Probe ?? new WslToolProbeResult();
        bool hasCachedProbe = instance.Probe is not null;

        var (dialog, form, footer) = CreateDialog("配置开发环境", 640);

        // 精简实例标题与副标题（遵循 DESIGN.md 页头层级）
        var instanceTitle = new TextBlock { Text = $"实例：{instance.Name}", FontWeight = FontWeights.SemiBold, FontSize = 15 };
        instanceTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        form.Children.Add(instanceTitle);

        var instanceSub = new TextBlock
        {
            Text = "适用于 APT 发行版 · 在 WSL 终端运行并自动验证已装版本",
            Margin = new Thickness(0, 2, 0, 12)
        };
        instanceSub.SetResourceReference(FrameworkElement.StyleProperty, "SettingsCardSubtitle");
        form.Children.Add(instanceSub);

        // 上下文 InfoBar（关联项目推荐 / 依赖联动 / 缺少 curl）
        var contextInfoBar = new InfoBar
        {
            IsOpen = false,
            IsClosable = false,
            Margin = new Thickness(0, 0, 0, 12)
        };
        form.Children.Add(contextInfoBar);

        // 分组 1：配置方案
        form.Children.Add(CreateGroupHeading("配置方案"));

        var presetCombo = new ComboBox
        {
            Width = 236,
            MinHeight = 32,
            MaxDropDownHeight = 320,
            VerticalAlignment = VerticalAlignment.Center,
            ItemTemplate = CreatePresetItemTemplate()
        };
        App.SetUseNativeComboBoxMotion(presetCombo, true);
        System.Windows.Automation.AutomationProperties.SetName(presetCombo, "静态站架构预设");

        form.Children.Add(new CardControl
        {
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(14, 11, 14, 11),
            IsTabStop = false,
            Icon = new SymbolIcon { Symbol = SymbolRegular.Apps24 },
            Header = CreateCardHeader("静态站架构预设", "按架构展示对应工具链；带荧光点表示该环境已在实例就绪"),
            Content = presetCombo
        });

        var userBox = new TextBox
        {
            Width = 236,
            MinHeight = 32,
            PlaceholderText = "默认用户"
        };
        System.Windows.Automation.AutomationProperties.SetName(userBox, "Linux 用户（留空使用默认用户）");
        form.Children.Add(new CardControl
        {
            Margin = new Thickness(0, 0, 0, 14),
            Padding = new Thickness(14, 11, 14, 11),
            IsTabStop = false,
            Icon = new SymbolIcon { Symbol = SymbolRegular.Person24 },
            Header = CreateCardHeader("Linux 用户（可选）", "留空使用该发行版的默认用户执行安装"),
            Content = userBox
        });

        // 动态工具链分组头（筛选态：单标题 + 「查看全部工具链」；自定义态：双分组）
        var filteredHeaderRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var showAllButton = new Button
        {
            Content = "查看全部工具链",
            Appearance = ControlAppearance.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2, 6, 2),
            MinHeight = 24,
            FontSize = 12
        };
        showAllButton.SetResourceReference(Control.FontFamilyProperty, "WorkspaceSmallTypeface");
        showAllButton.SetResourceReference(Control.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        DockPanel.SetDock(showAllButton, Dock.Right);
        filteredHeaderRow.Children.Add(showAllButton);

        var filteredHeadingText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        filteredHeadingText.SetResourceReference(FrameworkElement.StyleProperty, "SettingsGroupHeader");
        filteredHeadingText.Margin = new Thickness(0);
        filteredHeaderRow.Children.Add(filteredHeadingText);
        form.Children.Add(filteredHeaderRow);

        var customCoreHeading = CreateGroupHeading("基础与 Node.js 生态");
        form.Children.Add(customCoreHeading);

        // --- 1. Git（标准单行 CardControl） ---
        var gitVersion = CreateVersionCombo("Git", "latest");
        var gitSwitch = CreateCompactSwitch("安装 Git");
        var gitStatusHost = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
        var gitSubBadge = CreateSummaryBadge();
        var gitRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        gitRight.Children.Add(gitVersion);
        gitRight.Children.Add(gitSwitch);
        var gitCard = new CardControl
        {
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(14, 11, 14, 11),
            IsTabStop = false,
            Icon = CreateToolIconElement("git", darkTheme),
            Header = CreateToolHeader("Git", "通过 APT 安装版本控制工具", gitStatusHost, gitSubBadge),
            Content = gitRight
        };
        form.Children.Add(gitCard);
        var gitSlot = new ToolCardSlot
        {
            Group = WslToolGroup.Git,
            Root = gitCard,
            Switch = gitSwitch,
            VersionBox = gitVersion,
            StatusBadgeHost = gitStatusHost,
            SubSummaryBadge = gitSubBadge
        };

        // --- 2. Node.js (nvm) SettingsCardExpander ---
        var nodeVersion = CreateVersionCombo("Node.js", "lts/*", "latest");
        var pnpmCheck = new CheckBox { Content = "安装 pnpm", VerticalAlignment = VerticalAlignment.Center };
        var pnpmInstalledBadgeHost = new ContentControl { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var pnpmVersion = CreateVersionCombo("pnpm", "latest");
        var corepackCheck = new CheckBox { Content = "启用 Corepack（提供 Yarn 支持）", Margin = new Thickness(0, 8, 0, 0) };

        var nodeBody = new StackPanel();
        nodeBody.Children.Add(CreateOptionRow("Node.js 版本", nodeVersion));
        nodeBody.Children.Add(CreateSubDivider());
        var pnpmLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        pnpmLeft.Children.Add(pnpmCheck);
        pnpmLeft.Children.Add(pnpmInstalledBadgeHost);
        nodeBody.Children.Add(CreateOptionRow(pnpmLeft, pnpmVersion));
        nodeBody.Children.Add(corepackCheck);

        var nodeSlot = CreateExpanderSlot(
            WslToolGroup.Node, "node", darkTheme,
            "Node.js（nvm）", "覆盖 VuePress / VitePress / Hexo / Astro / Docusaurus / Nuxt / Next.js 等 10 种架构",
            nodeVersion, nodeBody);
        form.Children.Add(nodeSlot.Root);

        // 自定义态下的第二分组标题
        var customExtraHeading = CreateGroupHeading("其他静态站工具链");
        customExtraHeading.Margin = new Thickness(0, 6, 0, 8);
        form.Children.Add(customExtraHeading);

        // --- 3. Python 3 (MkDocs) SettingsCardExpander ---
        var pythonVersion = CreateVersionCombo("Python 3", "latest");
        var pyPipVenvCheck = new CheckBox { Content = "同时安装 pip 与 venv（python3-pip / python3-venv，MkDocs 必需）", IsChecked = true, Margin = new Thickness(0, 2, 0, 0) };
        var pyPipxCheck = new CheckBox { Content = "安装 pipx（用于隔离安装 mkdocs / mkdocs-material）", IsChecked = true, Margin = new Thickness(0, 6, 0, 0) };
        var pyAliasCheck = new CheckBox { Content = "同时提供 python 命令（python-is-python3）", Margin = new Thickness(0, 6, 0, 0) };

        var pyBody = new StackPanel();
        pyBody.Children.Add(CreateOptionRow("Python 3 版本（APT）", pythonVersion));
        pyBody.Children.Add(CreateSubDivider());
        pyBody.Children.Add(pyPipVenvCheck);
        pyBody.Children.Add(pyPipxCheck);
        pyBody.Children.Add(pyAliasCheck);

        var pythonSlot = CreateExpanderSlot(
            WslToolGroup.Python, "python3", darkTheme,
            "Python 3（MkDocs）", "通过 APT 安装 Python 运行时与虚拟环境（适配 PEP 668）",
            pythonVersion, pyBody);
        form.Children.Add(pythonSlot.Root);

        // --- 4. Hugo Extended SettingsCardExpander ---
        var hugoVersion = CreateVersionCombo("Hugo Extended", "latest");
        var goCheck = new CheckBox { Content = "同时安装 Go（仅 Hugo Modules 主题需要）", VerticalAlignment = VerticalAlignment.Center };
        var goVersion = CreateVersionCombo("Go", "latest");

        var hugoBody = new StackPanel();
        hugoBody.Children.Add(CreateOptionRow("Hugo Extended 版本", hugoVersion));
        hugoBody.Children.Add(CreateSubDivider());
        hugoBody.Children.Add(CreateOptionRow(goCheck, goVersion));

        var hugoSlot = CreateExpanderSlot(
            WslToolGroup.Hugo, "hugo", darkTheme,
            "Hugo Extended", "安装官方 Extended 版至 /usr/local/bin（含 SCSS / WebP），按需附带 Go",
            hugoVersion, hugoBody);
        form.Children.Add(hugoSlot.Root);

        // --- 5. Ruby & Bundler (Jekyll) SettingsCardExpander ---
        var rubyVersion = CreateVersionCombo("Ruby", "latest");
        var rubyNote = new TextBlock
        {
            Text = "将自动补装 build-essential、zlib1g-dev 与 bundler，并配置用户级 ~/.gems，后续 bundle install 无需 sudo。",
            TextWrapping = TextWrapping.Wrap
        };
        rubyNote.SetResourceReference(FrameworkElement.StyleProperty, "SettingsCardSubtitle");

        var rubyBody = new StackPanel();
        rubyBody.Children.Add(CreateOptionRow("Ruby 版本（APT）", rubyVersion));
        rubyBody.Children.Add(CreateSubDivider());
        rubyBody.Children.Add(rubyNote);

        var rubySlot = CreateExpanderSlot(
            WslToolGroup.Ruby, "ruby", darkTheme,
            "Ruby 与 Bundler（Jekyll）", "安装 ruby-full、编译工具链与 Bundler，自动配置用户级 ~/.gems",
            rubyVersion, rubyBody);
        form.Children.Add(rubySlot.Root);

        // --- 6. mdBook (Rust) SettingsCardExpander ---
        var mdbookVersion = CreateVersionCombo("mdBook", "latest");
        var rustCheck = new CheckBox { Content = "同时安装 Rust（rustup / cargo，仅需编译 mdbook-mermaid 等插件时勾选）" };

        var mdbookBody = new StackPanel();
        mdbookBody.Children.Add(CreateOptionRow("mdBook 预编译版本", mdbookVersion));
        mdbookBody.Children.Add(CreateSubDivider());
        mdbookBody.Children.Add(rustCheck);

        var mdbookSlot = CreateExpanderSlot(
            WslToolGroup.MdBook, "mdbook", darkTheme,
            "mdBook（Rust）", "下载官方预编译二进制至 /usr/local/bin，无需编译、秒级就绪",
            mdbookVersion, mdbookBody);
        form.Children.Add(mdbookSlot.Root);

        var allSlots = new Dictionary<WslToolGroup, ToolCardSlot>
        {
            [WslToolGroup.Git] = gitSlot,
            [WslToolGroup.Node] = nodeSlot,
            [WslToolGroup.Python] = pythonSlot,
            [WslToolGroup.Hugo] = hugoSlot,
            [WslToolGroup.Ruby] = rubySlot,
            [WslToolGroup.MdBook] = mdbookSlot
        };

        // 懒加载版本状态行
        var lazyStatusRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed
        };
        lazyStatusRow.Children.Add(new ProgressRing
        {
            Width = 14,
            Height = 14,
            IsIndeterminate = true,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        var lazyStatusText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        lazyStatusText.SetResourceReference(FrameworkElement.StyleProperty, "SettingsCardSubtitle");
        lazyStatusRow.Children.Add(lazyStatusText);
        form.Children.Add(lazyStatusRow);

        // 底部固定栏：摘要 + sudo 徽章 + 操作按钮
        var summaryRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var sudoBadge = new Badge { VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(sudoBadge, Dock.Right);
        summaryRow.Children.Add(sudoBadge);

        var summaryText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        summaryText.SetResourceReference(FrameworkElement.StyleProperty, "SettingsCardSubtitle");
        Typography.SetNumeralAlignment(summaryText, FontNumeralAlignment.Tabular);
        summaryRow.Children.Add(summaryText);
        footer.Children.Add(summaryRow);

        var validationText = new TextBlock
        {
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = Visibility.Collapsed
        };
        validationText.SetResourceReference(FrameworkElement.StyleProperty, "SettingsCardSubtitle");
        validationText.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        footer.Children.Add(validationText);

        var footerButtons = new DockPanel();
        var clearButton = new Button
        {
            Content = "清空选择",
            Appearance = ControlAppearance.Secondary,
            MinWidth = 84
        };
        clearButton.SetResourceReference(FrameworkElement.StyleProperty, "WorkspaceTextButton");
        DockPanel.SetDock(clearButton, Dock.Left);
        footerButtons.Children.Add(clearButton);

        var rightActions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var cancelButton = new Button { Content = "取消", IsCancel = true, MinWidth = 84, Margin = new Thickness(0, 0, 8, 0) };
        cancelButton.SetResourceReference(FrameworkElement.StyleProperty, "WorkspaceTextButton");
        var installButton = new Button { Content = "在终端安装", Appearance = ControlAppearance.Primary, MinWidth = 104 };
        installButton.SetResourceReference(FrameworkElement.StyleProperty, "WorkspaceTextButton");
        rightActions.Children.Add(cancelButton);
        rightActions.Children.Add(installButton);
        footerButtons.Children.Add(rightActions);
        footer.Children.Add(footerButtons);

        // --- 状态与联动控制器 ---
        bool closed = false;
        bool suppressEvents = false;
        string? linkageHint = null;
        int activeVersionFetches = 0;
        int probeGeneration = 0;
        var loadedComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dialogCts = new CancellationTokenSource();

        WslEnvironmentPreset currentPreset = linkedGroups.Count > 0
            ? WslEnvironmentPreset.Recommended
            : WslEnvironmentPreset.Node;

        WslDevelopmentSelection ReadSelection() => new(
            Git: gitSwitch.IsChecked == true,
            GitVersion: SelectedText(gitVersion, "latest"),
            Node: nodeSlot.Switch.IsChecked == true,
            NodeVersion: SelectedText(nodeVersion, "lts/*"),
            Pnpm: pnpmCheck.IsChecked == true,
            PnpmVersion: SelectedText(pnpmVersion, "latest"),
            Corepack: corepackCheck.IsChecked == true,
            Python: pythonSlot.Switch.IsChecked == true,
            PythonVersion: SelectedText(pythonVersion, "latest"),
            PythonPipVenv: pythonSlot.Switch.IsChecked == true && pyPipVenvCheck.IsChecked == true,
            PythonPipx: pythonSlot.Switch.IsChecked == true && pyPipxCheck.IsChecked == true,
            PythonAlias: pyAliasCheck.IsChecked == true,
            Hugo: hugoSlot.Switch.IsChecked == true,
            HugoVersion: SelectedText(hugoVersion, "latest"),
            Go: goCheck.IsChecked == true,
            GoVersion: SelectedText(goVersion, "latest"),
            Ruby: rubySlot.Switch.IsChecked == true,
            RubyVersion: SelectedText(rubyVersion, "latest"),
            MdBook: mdbookSlot.Switch.IsChecked == true,
            MdBookVersion: SelectedText(mdbookVersion, "latest"),
            Rust: rustCheck.IsChecked == true);

        void RebuildPresetDropdown()
        {
            var choices = new List<PresetChoice>();
            if (linkedGroups.Count > 0)
            {
                choices.Add(new PresetChoice
                {
                    Preset = WslEnvironmentPreset.Recommended,
                    Label = $"关联项目推荐（{linkedProjects.Length} 个项目）",
                    Activation = WslDevelopmentEnvironment.GetPresetActivation(WslEnvironmentPreset.Recommended, currentProbe, linkedFrameworks)
                });
            }
            choices.AddRange(new[]
            {
                new PresetChoice
                {
                    Preset = WslEnvironmentPreset.Node,
                    Label = "Node.js 站点（10 种框架）",
                    Activation = WslDevelopmentEnvironment.GetPresetActivation(WslEnvironmentPreset.Node, currentProbe, linkedFrameworks)
                },
                new PresetChoice
                {
                    Preset = WslEnvironmentPreset.Hugo,
                    Label = "Hugo（Extended）",
                    Activation = WslDevelopmentEnvironment.GetPresetActivation(WslEnvironmentPreset.Hugo, currentProbe, linkedFrameworks)
                },
                new PresetChoice
                {
                    Preset = WslEnvironmentPreset.MkDocs,
                    Label = "MkDocs（Python 3）",
                    Activation = WslDevelopmentEnvironment.GetPresetActivation(WslEnvironmentPreset.MkDocs, currentProbe, linkedFrameworks)
                },
                new PresetChoice
                {
                    Preset = WslEnvironmentPreset.Jekyll,
                    Label = "Jekyll（Ruby）",
                    Activation = WslDevelopmentEnvironment.GetPresetActivation(WslEnvironmentPreset.Jekyll, currentProbe, linkedFrameworks)
                },
                new PresetChoice
                {
                    Preset = WslEnvironmentPreset.MdBook,
                    Label = "mdBook（预编译）",
                    Activation = WslDevelopmentEnvironment.GetPresetActivation(WslEnvironmentPreset.MdBook, currentProbe, linkedFrameworks)
                },
                new PresetChoice
                {
                    Preset = WslEnvironmentPreset.AllMissing,
                    Label = "全量补齐缺失环境",
                    Activation = PresetActivation.None
                },
                new PresetChoice
                {
                    Preset = WslEnvironmentPreset.Custom,
                    Label = "自定义（显示全部工具链）",
                    Activation = PresetActivation.None
                }
            });

            suppressEvents = true;
            presetCombo.ItemsSource = choices;
            presetCombo.SelectedItem = choices.FirstOrDefault(c => c.Preset == currentPreset) ?? choices[^1];
            suppressEvents = false;
        }

        void UpdateStatusBadges()
        {
            gitStatusHost.Content = CreateStatusBadge(currentProbe.GitVersion);
            nodeSlot.StatusBadgeHost.Content = CreateStatusBadge(currentProbe.NodeVersion);
            pnpmInstalledBadgeHost.Content = currentProbe.PnpmVersion is not null
                ? CreateStatusBadge(currentProbe.PnpmVersion)
                : null;

            string? pyWarn = currentProbe.PythonVersion is not null && !currentProbe.PythonReadyForMkDocs
                ? $"已装 {currentProbe.PythonVersion} · 缺 pip/venv"
                : null;
            pythonSlot.StatusBadgeHost.Content = CreateStatusBadge(currentProbe.PythonVersion, pyWarn);

            string? hugoWarn = currentProbe.HugoVersion is not null && !currentProbe.HugoIsExtended
                ? $"已装 {currentProbe.HugoVersion} · 非 Extended"
                : null;
            hugoSlot.StatusBadgeHost.Content = CreateStatusBadge(currentProbe.HugoVersion, hugoWarn);

            string? rubyWarn = currentProbe.RubyVersion is not null && !currentProbe.HasBundler
                ? $"已装 {currentProbe.RubyVersion} · 缺 Bundler"
                : null;
            rubySlot.StatusBadgeHost.Content = CreateStatusBadge(currentProbe.RubyVersion, rubyWarn);

            mdbookSlot.StatusBadgeHost.Content = CreateStatusBadge(currentProbe.MdBookVersion);
        }

        void UpdateCardVisibility()
        {
            var visible = new HashSet<WslToolGroup>(WslDevelopmentEnvironment.GetVisibleGroups(currentPreset, currentProbe, linkedFrameworks));
            bool isCustom = currentPreset == WslEnvironmentPreset.Custom;

            filteredHeaderRow.Visibility = isCustom ? Visibility.Collapsed : Visibility.Visible;
            customCoreHeading.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            customExtraHeading.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;

            if (!isCustom && presetCombo.SelectedItem is PresetChoice choice)
                filteredHeadingText.Text = $"所需工具链 · {choice.Label}";

            foreach (var (group, slot) in allSlots)
                slot.Root.Visibility = visible.Contains(group) ? Visibility.Visible : Visibility.Collapsed;
        }

        void UpdateSubBadgesAndControls()
        {
            var sel = ReadSelection();

            gitVersion.IsEnabled = sel.Git;
            nodeVersion.IsEnabled = sel.Node;
            pnpmVersion.IsEnabled = sel.Pnpm;
            pythonVersion.IsEnabled = sel.Python;
            pyPipVenvCheck.IsEnabled = sel.Python;
            pyPipxCheck.IsEnabled = sel.Python;
            hugoVersion.IsEnabled = sel.Hugo;
            goVersion.IsEnabled = sel.Go;
            rubyVersion.IsEnabled = sel.Ruby;
            mdbookVersion.IsEnabled = sel.MdBook;

            SetSummaryBadge(gitSubBadge, sel.Git ? sel.GitVersion : null);

            string nodeSub = string.Join(" · ", new[]
            {
                sel.Node ? sel.NodeVersion : "",
                sel.Pnpm ? "pnpm" : "",
                sel.Corepack ? "Corepack" : ""
            }.Where(s => s.Length > 0));
            SetSummaryBadge(nodeSlot.SubSummaryBadge, nodeSub);

            string pySub = sel.Python
                ? string.Join(" + ", new[] { sel.PythonPipVenv ? "pip/venv" : "", sel.PythonPipx ? "pipx" : "", sel.PythonAlias ? "python" : "" }.Where(s => s.Length > 0))
                : (sel.PythonAlias ? "python别名" : "");
            SetSummaryBadge(pythonSlot.SubSummaryBadge, pySub);

            string hugoSub = string.Join(" · ", new[] { sel.Hugo ? sel.HugoVersion : "", sel.Go ? "含 Go" : "" }.Where(s => s.Length > 0));
            SetSummaryBadge(hugoSlot.SubSummaryBadge, hugoSub);

            SetSummaryBadge(rubySlot.SubSummaryBadge, sel.Ruby ? sel.RubyVersion : null);

            string mdSub = string.Join(" · ", new[] { sel.MdBook ? sel.MdBookVersion : "", sel.Rust ? "含 Rust" : "" }.Where(s => s.Length > 0));
            SetSummaryBadge(mdbookSlot.SubSummaryBadge, mdSub);

            // 更新顶部上下文 InfoBar
            bool needsCurl = !currentProbe.HasDownloadTool && WslDevelopmentEnvironment.NeedsDownloadTool(sel);
            if (!string.IsNullOrWhiteSpace(linkageHint))
            {
                contextInfoBar.ShowFeedback(linkageHint, InfoBarSeverity.Informational);
            }
            else if (needsCurl)
            {
                contextInfoBar.ShowFeedback("当前实例缺少下载工具，安装前将自动补装 curl 与 CA 证书。", InfoBarSeverity.Warning);
            }
            else if (currentPreset == WslEnvironmentPreset.Recommended && linkedProjects.Length > 0)
            {
                string summary = string.Join("、", linkedProjects.Select(p =>
                    p.Framework == SiteFramework.Generic ? p.Name : $"{p.Name}（{SiteFrameworkDetector.GetDisplayName(p.Framework)}）"));
                contextInfoBar.ShowFeedback($"关联项目：{summary}。仅展示所需工具链并预选缺失项。", InfoBarSeverity.Informational);
            }
            else
            {
                contextInfoBar.IsOpen = false;
            }

            // 底栏摘要与 sudo 提示
            var described = WslDevelopmentEnvironment.DescribeSelection(sel);
            string login = userBox.Text.Trim();
            bool userValid = login.Length == 0 || SyncPath.IsValidWslUser(login);
            bool selectionValid = WslDevelopmentEnvironment.IsValidSelection(sel);

            if (described.Count == 0)
            {
                summaryText.Text = "当前筛选下的必需组件均已就绪（或尚未勾选要安装的项）。";
                sudoBadge.Visibility = Visibility.Collapsed;
            }
            else
            {
                summaryText.Text = $"已选 {described.Count} 项：{string.Join("、", described)}";
                bool needsSudo = WslDevelopmentEnvironment.RequiresSudo(sel, currentProbe);
                sudoBadge.Content = needsSudo ? "需要 sudo 密码" : "纯用户态 · 无需 sudo";
                sudoBadge.Appearance = needsSudo ? ControlAppearance.Caution : ControlAppearance.Success;
                sudoBadge.Visibility = Visibility.Visible;
            }

            if (!userValid)
            {
                validationText.Text = "Linux 用户名格式不正确（仅允许小写字母、数字、下划线和连字符）。";
                validationText.Visibility = Visibility.Visible;
            }
            else
            {
                validationText.Visibility = Visibility.Collapsed;
            }

            installButton.IsEnabled = userValid && selectionValid;
        }

        async Task EnsureComponentVersionsAsync(string key, ComboBox target, string displayName)
        {
            if (closed || !loadedComponents.Add(key))
                return;

            Interlocked.Increment(ref activeVersionFetches);
            lazyStatusText.Text = $"正在按需读取 {displayName} 可用版本…";
            lazyStatusRow.Visibility = Visibility.Visible;
            try
            {
                string[] versions = await LoadComponentVersionsAsync(instance.Name, key, dialogCts.Token);
                if (closed || dialogCts.IsCancellationRequested || versions.Length == 0)
                    return;

                string selected = SelectedText(target, "latest");
                target.ItemsSource = target.Items.Cast<string>().Concat(versions).Distinct(StringComparer.Ordinal).ToArray();
                target.SelectedItem = selected;
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or TimeoutException or JsonException or System.ComponentModel.Win32Exception or System.IO.IOException or InvalidOperationException or KeyNotFoundException)
            {
            }
            finally
            {
                if (!closed && Interlocked.Decrement(ref activeVersionFetches) <= 0)
                    lazyStatusRow.Visibility = Visibility.Collapsed;
            }
        }

        void TriggerLazyLoadsForSelection(WslDevelopmentSelection sel)
        {
            if (sel.Git) _ = EnsureComponentVersionsAsync("git", gitVersion, "Git");
            if (sel.Node) _ = EnsureComponentVersionsAsync("node", nodeVersion, "Node.js");
            if (sel.Pnpm) _ = EnsureComponentVersionsAsync("pnpm", pnpmVersion, "pnpm");
            if (sel.Python) _ = EnsureComponentVersionsAsync("python", pythonVersion, "Python 3");
            if (sel.Hugo) _ = EnsureComponentVersionsAsync("hugo", hugoVersion, "Hugo Extended");
            if (sel.Go) _ = EnsureComponentVersionsAsync("go", goVersion, "Go");
            if (sel.Ruby) _ = EnsureComponentVersionsAsync("ruby", rubyVersion, "Ruby");
            if (sel.MdBook) _ = EnsureComponentVersionsAsync("mdbook", mdbookVersion, "mdBook");
        }

        void ApplyPreset(WslEnvironmentPreset preset, bool silent = false)
        {
            currentPreset = preset;
            linkageHint = null;

            if (preset != WslEnvironmentPreset.Custom)
            {
                var presetSelection = WslDevelopmentEnvironment.BuildPresetSelection(preset, currentProbe, linkedFrameworks);
                suppressEvents = true;
                gitSwitch.IsChecked = presetSelection.Git;
                nodeSlot.Switch.IsChecked = presetSelection.Node;
                pnpmCheck.IsChecked = presetSelection.Pnpm;
                corepackCheck.IsChecked = presetSelection.Corepack;
                pythonSlot.Switch.IsChecked = presetSelection.Python;
                pyPipVenvCheck.IsChecked = presetSelection.Python ? presetSelection.PythonPipVenv : true;
                pyPipxCheck.IsChecked = presetSelection.Python ? presetSelection.PythonPipx : true;
                pyAliasCheck.IsChecked = presetSelection.PythonAlias;
                hugoSlot.Switch.IsChecked = presetSelection.Hugo;
                goCheck.IsChecked = presetSelection.Go;
                rubySlot.Switch.IsChecked = presetSelection.Ruby;
                mdbookSlot.Switch.IsChecked = presetSelection.MdBook;
                rustCheck.IsChecked = presetSelection.Rust;

                bool singleFocus = preset is WslEnvironmentPreset.Node or WslEnvironmentPreset.Hugo
                    or WslEnvironmentPreset.MkDocs or WslEnvironmentPreset.Jekyll
                    or WslEnvironmentPreset.MdBook or WslEnvironmentPreset.Recommended;

                nodeSlot.Expander!.IsExpanded = singleFocus && (preset == WslEnvironmentPreset.Node || presetSelection.Node || presetSelection.Pnpm);
                pythonSlot.Expander!.IsExpanded = singleFocus && (preset == WslEnvironmentPreset.MkDocs || presetSelection.Python);
                hugoSlot.Expander!.IsExpanded = singleFocus && (preset == WslEnvironmentPreset.Hugo || presetSelection.Hugo);
                rubySlot.Expander!.IsExpanded = singleFocus && (preset == WslEnvironmentPreset.Jekyll || presetSelection.Ruby);
                mdbookSlot.Expander!.IsExpanded = singleFocus && (preset == WslEnvironmentPreset.MdBook || presetSelection.MdBook);
                suppressEvents = false;

                if (!silent && preset != WslEnvironmentPreset.AllMissing &&
                    WslDevelopmentEnvironment.GetPresetActivation(preset, currentProbe, linkedFrameworks) == PresetActivation.Active)
                {
                    linkageHint = "该环境已在当前 WSL 实例激活就绪；如需升级或增装子组件，可直接开启对应开关。";
                }
            }

            RebuildPresetDropdown();
            UpdateStatusBadges();
            UpdateCardVisibility();
            UpdateSubBadgesAndControls();
            TriggerLazyLoadsForSelection(ReadSelection());
        }

        async Task RefreshProbeAsync(bool applyInitialPreset)
        {
            int gen = ++probeGeneration;
            string login = userBox.Text.Trim();
            if (login.Length > 0 && !SyncPath.IsValidWslUser(login))
                return;

            var (probe, _, _) = await ReadInstalledToolsAsync(instance.Name, login.Length > 0 ? login : null, dialogCts.Token);
            if (closed || gen != probeGeneration || probe is null)
                return;

            currentProbe = probe;
            if (applyInitialPreset)
                ApplyPreset(currentPreset, silent: true);
            else
            {
                RebuildPresetDropdown();
                UpdateStatusBadges();
                UpdateCardVisibility();
                UpdateSubBadgesAndControls();
            }
        }

        // --- 事件绑定 ---
        presetCombo.SelectionChanged += (_, _) =>
        {
            if (suppressEvents || presetCombo.SelectedItem is not PresetChoice choice)
                return;
            ApplyPreset(choice.Preset);
        };

        showAllButton.Click += (_, _) => ApplyPreset(WslEnvironmentPreset.Custom);

        clearButton.Click += (_, _) =>
        {
            suppressEvents = true;
            gitSwitch.IsChecked = false;
            nodeSlot.Switch.IsChecked = false;
            pnpmCheck.IsChecked = false;
            corepackCheck.IsChecked = false;
            pythonSlot.Switch.IsChecked = false;
            pyAliasCheck.IsChecked = false;
            hugoSlot.Switch.IsChecked = false;
            goCheck.IsChecked = false;
            rubySlot.Switch.IsChecked = false;
            mdbookSlot.Switch.IsChecked = false;
            rustCheck.IsChecked = false;
            suppressEvents = false;
            linkageHint = null;
            UpdateSubBadgesAndControls();
        };

        void WireMainSwitch(ToolCardSlot slot, string versionKey, string displayName, Action<bool>? onToggled = null)
        {
            void Handle(bool isChecked)
            {
                if (suppressEvents) return;
                linkageHint = null;
                if (slot.Expander is not null)
                    slot.Expander.IsExpanded = isChecked;
                onToggled?.Invoke(isChecked);
                if (isChecked)
                    _ = EnsureComponentVersionsAsync(versionKey, slot.VersionBox, displayName);
                UpdateSubBadgesAndControls();
            }
            slot.Switch.Checked += (_, _) => Handle(true);
            slot.Switch.Unchecked += (_, _) => Handle(false);
        }

        WireMainSwitch(gitSlot, "git", "Git");
        WireMainSwitch(nodeSlot, "node", "Node.js", isChecked =>
        {
            suppressEvents = true;
            if (isChecked && currentProbe.PnpmVersion is null)
            {
                pnpmCheck.IsChecked = true;
                _ = EnsureComponentVersionsAsync("pnpm", pnpmVersion, "pnpm");
            }
            else if (!isChecked && !currentProbe.HasNvm && (pnpmCheck.IsChecked == true || corepackCheck.IsChecked == true))
            {
                pnpmCheck.IsChecked = false;
                corepackCheck.IsChecked = false;
                linkageHint = "关闭 Node.js（nvm）时，已同步取消依赖它的 pnpm / Corepack。";
            }
            suppressEvents = false;
        });
        WireMainSwitch(pythonSlot, "python", "Python 3");
        WireMainSwitch(hugoSlot, "hugo", "Hugo Extended");
        WireMainSwitch(rubySlot, "ruby", "Ruby");
        WireMainSwitch(mdbookSlot, "mdbook", "mdBook");

        void WireNodeDependentCheck(CheckBox box, string? versionKey = null, ComboBox? versionTarget = null, string? displayName = null)
        {
            void Handle()
            {
                if (suppressEvents) return;
                linkageHint = null;
                if (box.IsChecked == true)
                {
                    if (!currentProbe.HasNvm && nodeSlot.Switch.IsChecked != true)
                    {
                        suppressEvents = true;
                        nodeSlot.Switch.IsChecked = true;
                        nodeSlot.Expander!.IsExpanded = true;
                        suppressEvents = false;
                        linkageHint = "当前实例未安装 nvm，已自动开启 Node.js（nvm）以满足依赖。";
                        _ = EnsureComponentVersionsAsync("node", nodeVersion, "Node.js");
                    }
                    if (versionKey is not null && versionTarget is not null && displayName is not null)
                        _ = EnsureComponentVersionsAsync(versionKey, versionTarget, displayName);
                }
                UpdateSubBadgesAndControls();
            }
            box.Checked += (_, _) => Handle();
            box.Unchecked += (_, _) => Handle();
        }

        WireNodeDependentCheck(pnpmCheck, "pnpm", pnpmVersion, "pnpm");
        WireNodeDependentCheck(corepackCheck);

        foreach (var check in new[] { pyPipVenvCheck, pyPipxCheck, pyAliasCheck, rustCheck })
        {
            check.Checked += (_, _) => { if (!suppressEvents) UpdateSubBadgesAndControls(); };
            check.Unchecked += (_, _) => { if (!suppressEvents) UpdateSubBadgesAndControls(); };
        }

        goCheck.Checked += (_, _) =>
        {
            if (suppressEvents) return;
            _ = EnsureComponentVersionsAsync("go", goVersion, "Go");
            UpdateSubBadgesAndControls();
        };
        goCheck.Unchecked += (_, _) => { if (!suppressEvents) UpdateSubBadgesAndControls(); };

        // 版本下拉框展开时按需懒加载 + 切换版本更新摘要
        void WireVersionBox(ComboBox box, string key, string displayName)
        {
            box.DropDownOpened += (_, _) => _ = EnsureComponentVersionsAsync(key, box, displayName);
            box.SelectionChanged += (_, _) => { if (!suppressEvents) UpdateSubBadgesAndControls(); };
        }

        WireVersionBox(gitVersion, "git", "Git");
        WireVersionBox(nodeVersion, "node", "Node.js");
        WireVersionBox(pnpmVersion, "pnpm", "pnpm");
        WireVersionBox(pythonVersion, "python", "Python 3");
        WireVersionBox(hugoVersion, "hugo", "Hugo Extended");
        WireVersionBox(goVersion, "go", "Go");
        WireVersionBox(rubyVersion, "ruby", "Ruby");
        WireVersionBox(mdbookVersion, "mdbook", "mdBook");

        userBox.TextChanged += (_, _) => UpdateSubBadgesAndControls();
        userBox.LostKeyboardFocus += async (_, _) =>
        {
            if (!closed)
                await RefreshProbeAsync(applyInitialPreset: false);
        };

        dialog.Closed += (_, _) =>
        {
            closed = true;
            try { dialogCts.Cancel(); } catch (ObjectDisposedException) { }
        };

        dialog.Loaded += async (_, _) =>
        {
            if (!hasCachedProbe)
                await RefreshProbeAsync(applyInitialPreset: true);
        };

        installButton.Click += (_, _) =>
        {
            var sel = ReadSelection();
            string login = userBox.Text.Trim();
            if ((login.Length > 0 && !SyncPath.IsValidWslUser(login)) || !WslDevelopmentEnvironment.IsValidSelection(sel))
            {
                validationText.Text = "请至少勾选一项组件，并检查 Linux 用户名与版本格式。";
                validationText.Visibility = Visibility.Visible;
                return;
            }
            dialog.DialogResult = true;
        };

        // 初始渲染
        ApplyPreset(currentPreset, silent: true);

        bool accepted = dialog.ShowDialog() == true;
        dialogCts.Dispose();
        if (!accepted) return;

        var finalSelection = ReadSelection();
        try
        {
            var (script, scriptArgs) = WslDevelopmentEnvironment.BuildSetupCommand(finalSelection);
            var start = new ProcessStartInfo(ProcessRunner.WslExecutablePath) { UseShellExecute = true };
            start.ArgumentList.Add("-d");
            start.ArgumentList.Add(instance.Name);
            if (!string.IsNullOrWhiteSpace(userBox.Text))
            {
                start.ArgumentList.Add("-u");
                start.ArgumentList.Add(userBox.Text.Trim());
            }
            start.ArgumentList.Add("--cd");
            start.ArgumentList.Add("~");
            start.ArgumentList.Add("--exec");
            start.ArgumentList.Add("bash");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(script);
            start.ArgumentList.Add("sitelift-setup");
            foreach (string arg in scriptArgs)
                start.ArgumentList.Add(arg);

            using var terminal = Process.Start(start) ?? throw new InvalidOperationException("无法启动 WSL 终端。");
            Status.ShowFeedback("安装进度和版本验证结果显示在 WSL 终端中，完成后可点击「刷新检测」更新环境状态。");
        }
        catch (Exception error)
        {
            Status.ShowFeedback(error.Message, InfoBarSeverity.Error);
        }
    }

    private static TextBlock CreateGroupHeading(string title)
    {
        var heading = new TextBlock { Text = title, Margin = new Thickness(0, 0, 0, 8) };
        heading.SetResourceReference(FrameworkElement.StyleProperty, "SettingsGroupHeader");
        return heading;
    }

    private static IconElement CreateToolIconElement(string tool, bool darkTheme) => new ImageIcon
    {
        Source = ResolveToolIcon(tool, darkTheme),
        Width = 20,
        Height = 20,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static ToggleSwitch CreateCompactSwitch(string accessibleName)
    {
        var sw = new ToggleSwitch
        {
            OnContent = "",
            OffContent = "",
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        System.Windows.Automation.AutomationProperties.SetName(sw, accessibleName);
        return sw;
    }

    private static ComboBox CreateVersionCombo(string toolName, params string[] defaults)
    {
        var combo = new ComboBox
        {
            ItemsSource = defaults,
            SelectedItem = defaults[0],
            Width = 160,
            MinHeight = 32,
            MaxDropDownHeight = 250,
            VerticalAlignment = VerticalAlignment.Center
        };
        App.SetUseNativeComboBoxMotion(combo, true);
        Typography.SetNumeralAlignment(combo, FontNumeralAlignment.Tabular);
        System.Windows.Automation.AutomationProperties.SetName(combo, toolName + "版本");
        return combo;
    }

    private static Badge CreateSummaryBadge() => new()
    {
        Appearance = ControlAppearance.Info,
        Visibility = Visibility.Collapsed,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(6, 0, 0, 0)
    };

    private static void SetSummaryBadge(Badge badge, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            badge.Visibility = Visibility.Collapsed;
            badge.Content = "";
        }
        else
        {
            badge.Content = text;
            badge.Visibility = Visibility.Visible;
        }
    }

    private static StackPanel CreateToolHeader(string title, string subtitle, ContentControl statusHost, Badge summaryBadge)
    {
        var titleBlock = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center };
        titleBlock.SetResourceReference(FrameworkElement.StyleProperty, "SettingsCardTitle");

        statusHost.Margin = new Thickness(8, 0, 0, 0);

        var topRow = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        topRow.Children.Add(titleBlock);
        topRow.Children.Add(statusHost);
        topRow.Children.Add(summaryBadge);

        var subBlock = new TextBlock { Text = subtitle, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
        subBlock.SetResourceReference(FrameworkElement.StyleProperty, "SettingsCardSubtitle");

        var header = new StackPanel();
        header.Children.Add(topRow);
        header.Children.Add(subBlock);
        return header;
    }

    private static ToolCardSlot CreateExpanderSlot(
        WslToolGroup group,
        string iconTool,
        bool darkTheme,
        string title,
        string subtitle,
        ComboBox versionBox,
        UIElement bodyContent)
    {
        var statusHost = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
        var summaryBadge = CreateSummaryBadge();
        var sw = CreateCompactSwitch("安装 " + title);
        sw.Margin = new Thickness(12, 0, 4, 0);

        var headerDock = new DockPanel();
        DockPanel.SetDock(sw, Dock.Right);
        headerDock.Children.Add(sw);
        headerDock.Children.Add(CreateToolHeader(title, subtitle, statusHost, summaryBadge));

        var expander = new CardExpander
        {
            Icon = CreateToolIconElement(iconTool, darkTheme),
            Header = headerDock,
            Content = bodyContent,
            IsExpanded = false
        };
        expander.SetResourceReference(FrameworkElement.StyleProperty, "SettingsCardExpander");

        return new ToolCardSlot
        {
            Group = group,
            Root = expander,
            Switch = sw,
            VersionBox = versionBox,
            StatusBadgeHost = statusHost,
            SubSummaryBadge = summaryBadge,
            Expander = expander
        };
    }

    private static DockPanel CreateOptionRow(object left, FrameworkElement right)
    {
        var row = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);

        if (left is string labelText)
        {
            var tb = new TextBlock { Text = labelText, VerticalAlignment = VerticalAlignment.Center };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            row.Children.Add(tb);
        }
        else if (left is UIElement element)
        {
            row.Children.Add(element);
        }
        return row;
    }

    private static Border CreateSubDivider()
    {
        var line = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 10, 0, 10)
        };
        line.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
        return line;
    }

    private static Badge CreateStatusBadge(string? installedVersion, string? warningText = null)
    {
        if (!string.IsNullOrWhiteSpace(warningText))
            return CreateGlowingBadge(warningText, ControlAppearance.Caution, isWarning: true);

        if (!string.IsNullOrWhiteSpace(installedVersion))
            return CreateGlowingBadge($"已装 {installedVersion}", ControlAppearance.Success, isWarning: false);

        return new Badge
        {
            Content = "未安装",
            Appearance = ControlAppearance.Secondary,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static Badge CreateGlowingBadge(string text, ControlAppearance appearance, bool isWarning)
    {
        var dot = new Ellipse
        {
            Width = 6,
            Height = 6,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect
            {
                Color = isWarning ? Color.FromRgb(0xF5, 0x9E, 0x0B) : Color.FromRgb(0x22, 0xC5, 0x5E),
                BlurRadius = 6,
                ShadowDepth = 0,
                Opacity = 0.85
            }
        };
        dot.SetResourceReference(Shape.FillProperty, isWarning ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush");

        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        Typography.SetNumeralAlignment(label, FontNumeralAlignment.Tabular);

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(dot);
        row.Children.Add(label);

        return new Badge
        {
            Content = row,
            Appearance = appearance,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static DataTemplate CreatePresetItemTemplate()
    {
        const string xaml = """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Grid Margin="0,1">
                <Grid.ColumnDefinitions>
                  <ColumnDefinition Width="14"/>
                  <ColumnDefinition Width="*"/>
                  <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>
                <Ellipse Width="7" Height="7" HorizontalAlignment="Left" VerticalAlignment="Center">
                  <Ellipse.Style>
                    <Style TargetType="Ellipse">
                      <Setter Property="Fill" Value="Transparent"/>
                      <Style.Triggers>
                        <DataTrigger Binding="{Binding IsActive}" Value="True">
                          <Setter Property="Fill" Value="{DynamicResource SystemFillColorSuccessBrush}"/>
                          <Setter Property="Effect">
                            <Setter.Value>
                              <DropShadowEffect Color="#22C55E" BlurRadius="7" ShadowDepth="0" Opacity="0.9"/>
                            </Setter.Value>
                          </Setter>
                        </DataTrigger>
                        <DataTrigger Binding="{Binding IsPartial}" Value="True">
                          <Setter Property="Fill" Value="{DynamicResource SystemFillColorCautionBrush}"/>
                          <Setter Property="Effect">
                            <Setter.Value>
                              <DropShadowEffect Color="#F59E0B" BlurRadius="7" ShadowDepth="0" Opacity="0.85"/>
                            </Setter.Value>
                          </Setter>
                        </DataTrigger>
                      </Style.Triggers>
                    </Style>
                  </Ellipse.Style>
                </Ellipse>
                <TextBlock Grid.Column="1" Text="{Binding Label}" VerticalAlignment="Center" TextTrimming="CharacterEllipsis"/>
                <TextBlock Grid.Column="2" Text="{Binding StatusTag}" Margin="10,0,0,0" VerticalAlignment="Center" FontSize="12">
                  <TextBlock.Style>
                    <Style TargetType="TextBlock">
                      <Setter Property="Visibility" Value="Collapsed"/>
                      <Style.Triggers>
                        <DataTrigger Binding="{Binding IsActive}" Value="True">
                          <Setter Property="Visibility" Value="Visible"/>
                          <Setter Property="Foreground" Value="{DynamicResource SystemFillColorSuccessBrush}"/>
                        </DataTrigger>
                        <DataTrigger Binding="{Binding IsPartial}" Value="True">
                          <Setter Property="Visibility" Value="Visible"/>
                          <Setter Property="Foreground" Value="{DynamicResource SystemFillColorCautionBrush}"/>
                        </DataTrigger>
                      </Style.Triggers>
                    </Style>
                  </TextBlock.Style>
                </TextBlock>
              </Grid>
            </DataTemplate>
            """;
        return (DataTemplate)XamlReader.Parse(xaml);
    }

    private static string SelectedText(ComboBox combo, string fallback) =>
        (combo.SelectedItem as string) is { Length: > 0 } value ? value : fallback;

    private static async Task<string[]> LoadComponentVersionsAsync(string instance, string key, CancellationToken token)
    {
        if (key is "git" or "python" or "ruby")
        {
            string pkg = key switch { "git" => "git", "python" => "python3", _ => "ruby-full" };
            return await ReadAptVersionsAsync(instance, pkg, token);
        }

        if (RemoteVersionCache.TryGetValue(key, out var cached) &&
            DateTimeOffset.UtcNow - cached.LoadedAt < RemoteVersionCacheTtl)
        {
            return cached.Versions;
        }

        string[] fetched = key switch
        {
            "node" => await ReadPublishedVersionsAsync(node: true, token),
            "pnpm" => await ReadPublishedVersionsAsync(node: false, token),
            "hugo" => await ReadGitHubReleaseTagsAsync("gohugoio/hugo", token),
            "mdbook" => await ReadGitHubReleaseTagsAsync("rust-lang/mdBook", token),
            "go" => await ReadGoVersionsAsync(token),
            _ => Array.Empty<string>()
        };

        if (fetched.Length > 0)
            RemoteVersionCache[key] = (DateTimeOffset.UtcNow, fetched);
        return fetched;
    }

    private static async Task<string[]> ReadAptVersionsAsync(string instance, string package, CancellationToken token)
    {
        var result = await InvokeAsync(new[] { "-d", instance, "--exec", "env", "LC_ALL=C", "apt-cache", "madison", package },
            encoding: System.Text.Encoding.UTF8, timeout: TimeSpan.FromSeconds(12), cancellation: token);
        if (result.Code != 0) return Array.Empty<string>();
        return result.Output.Split('\n').Select(line => line.Split('|'))
            .Where(fields => fields.Length == 3 && fields[0].Trim() == package)
            .Select(fields => fields[1].Trim()).Where(version => version.Length > 0).Distinct().ToArray();
    }

    private static async Task<string[]> ReadPublishedVersionsAsync(bool node, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, node
            ? "https://nodejs.org/dist/index.json"
            : "https://registry.npmjs.org/pnpm");
        if (!node) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.npm.install-v1+json"));
        using var response = await VersionClient.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        IEnumerable<string> versions = node
            ? json.RootElement.EnumerateArray().Select(item => item.GetProperty("version").GetString() ?? "")
            : json.RootElement.GetProperty("versions").EnumerateObject().Select(item => item.Name);
        return versions.Where(version => Version.TryParse(version.TrimStart('v'), out _))
            .OrderByDescending(version => Version.Parse(version.TrimStart('v')))
            .GroupBy(version => Version.Parse(version.TrimStart('v')).Major)
            .Take(6).SelectMany(group => group.Take(20)).ToArray();
    }

    private static async Task<string[]> ReadGitHubReleaseTagsAsync(string repo, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases?per_page=15");
        request.Headers.UserAgent.ParseAdd("SiteLift");
        using var response = await VersionClient.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (json.RootElement.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var tags = new List<string>();
        foreach (var item in json.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True)
                continue;
            if (item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                continue;
            if (item.TryGetProperty("tag_name", out var tagEl) && tagEl.ValueKind == JsonValueKind.String)
            {
                string tag = tagEl.GetString()?.Trim() ?? "";
                if (WslDevelopmentEnvironment.IsValidSemVerTag(tag) && tag != "latest")
                    tags.Add(tag);
            }
        }
        return tags.Distinct(StringComparer.Ordinal).Take(12).ToArray();
    }

    private static async Task<string[]> ReadGoVersionsAsync(CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://go.dev/dl/?mode=json");
        request.Headers.UserAgent.ParseAdd("SiteLift");
        using var response = await VersionClient.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (json.RootElement.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var list = new List<string>();
        foreach (var item in json.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("stable", out var stable) && stable.ValueKind == JsonValueKind.False)
                continue;
            if (item.TryGetProperty("version", out var verEl) && verEl.ValueKind == JsonValueKind.String)
            {
                string ver = verEl.GetString()?.Trim() ?? "";
                if (WslDevelopmentEnvironment.IsValidGoVersion(ver) && ver != "latest")
                    list.Add(ver.StartsWith("go", StringComparison.OrdinalIgnoreCase) ? ver[2..] : ver);
            }
        }
        return list.Distinct(StringComparer.Ordinal).Take(10).ToArray();
    }
}
