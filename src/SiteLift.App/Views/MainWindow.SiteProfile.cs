using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SiteLift.Core;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace SiteLift.App.Views;

public partial class MainWindow
{
    private async void ScanSiteProfileFromMenu(object sender, RoutedEventArgs e)
    {
        if (running || sender is not System.Windows.Controls.MenuItem { DataContext: ProjectEntry project })
            return;
        await ScanSiteProfileAsync(project);
    }

    private async void OpenSiteProfileFromMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: ProjectEntry project })
            return;

        try
        {
            var (scanRoot, displayRoot) = ResolveSiteProfileRoot(project);
            string profilePath = SiteProfileScanner.GetProfilePath(scanRoot);
            if (!await Task.Run(() => File.Exists(profilePath)))
            {
                HomeStatus.ShowFeedback($"「{project.Name}」尚未生成站点画像，请先点击快捷访问栏的「扫描站点画像」。", InfoBarSeverity.Informational);
                return;
            }

            using var opened = Process.Start(new ProcessStartInfo(profilePath)
            {
                UseShellExecute = true
            });
            _ = displayRoot;
        }
        catch (Exception error)
        {
            HomeStatus.ShowFeedback($"无法打开「{project.Name}」的站点画像：{error.Message}", InfoBarSeverity.Error);
        }
    }

    private async Task ScanSiteProfileAsync(ProjectEntry project)
    {
        if (running) return;

        string scanRoot;
        string displayRoot;
        try
        {
            (scanRoot, displayRoot) = ResolveSiteProfileRoot(project);
        }
        catch (Exception error)
        {
            HomeStatus.ShowFeedback($"无法扫描「{project.Name}」的站点画像：{error.Message}", InfoBarSeverity.Warning);
            return;
        }

        string[] pullPaths = ExtractConfiguredStringList(project.Configuration, "pullPaths", "pullPath");
        string[] excludePaths = ExtractConfiguredStringList(project.Configuration, "exclude");

        using var cancellation = new CancellationTokenSource();
        batchCancellation = cancellation;
        StopBatchButton.Content = "停止扫描";
        StopBatchButton.IsEnabled = true;
        SetActivityText(BatchProgressText, $"正在扫描「{project.Name}」站点画像");
        BatchProgressPanel.Visibility = Visibility.Visible;
        HomeStatus.IsOpen = false;
        running = true;
        IsRowSelectionEnabled = false;
        HomeActions.IsEnabled = false;
        UpdateHomeActions();
        ResetHomeLog();
        HomeResults.SelectedItem = HomeLogTab;
        SetResultsCollapsed(false);
        WriteHomeLog($"[{project.Name}] 扫描站点画像");
        WriteHomeLog($"扫描根目录：{displayRoot}");

        try
        {
            var scanOptions = new SiteProfileScanOptions(
                ConfigurationJson: project.Configuration,
                PullPaths: pullPaths,
                ExcludePaths: excludePaths,
                ProjectName: project.Name,
                EnsureGitExclude: true);

            var result = await Task.Run(
                () => SiteProfileScanner.ScanAndSave(scanRoot, scanOptions, cancellation.Token),
                cancellation.Token);

            var doc = result.Document;
            var auto = doc.AutoScanned;
            string themeSuffix = string.Equals(doc.Theme, "default", StringComparison.OrdinalIgnoreCase)
                ? ""
                : $"（主题：{doc.Theme}）";

            WriteHomeLog($"识别架构：{doc.Framework}{themeSuffix}");
            WriteHomeLog($"内容根目录：{string.Join("、", auto.ContentRoots)} | 命名风格：{auto.FileNaming} | Frontmatter：{auto.Frontmatter.Format}");
            WriteHomeLog($"扫描统计：{result.ScannedDirectories} 个目录，抽样 {result.SampledArticles} 篇文章，{result.CategoryCount} 组分类，{result.TagCount} 个标签");
            WriteHomeLog($"资源策略：{auto.Assets.Mode}（推荐目录：{auto.Assets.PreferredDir}） | 导航模式：{auto.Navigation.Mode}");
            if (auto.Navigation.IndexFiles.Length > 0)
                WriteHomeLog($"导航配置文件：{string.Join("、", auto.Navigation.IndexFiles)}");
            if (!string.IsNullOrWhiteSpace(auto.Navigation.Note))
                WriteHomeLog($"导航提示：{auto.Navigation.Note}");

            var overwrittenDirs = auto.Directories.Where(d => d.OverwrittenOnBuild).Select(d => d.Path).ToArray();
            if (overwrittenDirs.Length > 0)
                WriteHomeLog($"警告：以下目录位于 pullPaths，构建前会被远端覆盖，请勿直接投递文章：{string.Join("、", overwrittenDirs)}");

            string preserveNote = result.PreservedUserRules ? "（已无损保留 userRules）" : "";
            WriteHomeLog($"画像已写入：{result.RelativePath}{preserveNote}");

            if (!result.GitDirectoryFound)
                WriteHomeLog("Git 忽略：未检测到 .git 目录，已跳过 .git/info/exclude 更新。");
            else if (result.GitExcludeUpdated)
                WriteHomeLog($"Git 忽略：已自动将 {SiteProfileScanner.GitExcludeEntry} 追加到 .git/info/exclude。");
            else
                WriteHomeLog($"Git 忽略：{SiteProfileScanner.GitExcludeEntry} 已存在于 .git/info/exclude。");

            // Refresh detected framework icon in the project list if it was previously Generic.
            if (project.Framework == SiteFramework.Generic)
            {
                var detectedFramework = await Task.Run(() => SiteFrameworkDetector.DetectFromDirectory(scanRoot));
                if (detectedFramework != SiteFramework.Generic)
                    ApplyProjectFramework(project, detectedFramework);
            }

            string gitNote = result.GitExcludeUpdated ? "，已写入 .git/info/exclude" : "";
            HomeStatus.ShowFeedback(
                $"「{project.Name}」站点画像已生成（{doc.Framework}，{result.ScannedDirectories} 个目录 / {result.SampledArticles} 篇样本{gitNote}）。",
                InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            HomeStatus.ShowFeedback($"已停止「{project.Name}」的站点画像扫描。", InfoBarSeverity.Informational);
            WriteHomeLog("扫描已停止。");
        }
        catch (Exception error)
        {
            string message = $"站点画像扫描失败：{error.Message}";
            HomeStatus.ShowFeedback(message, InfoBarSeverity.Error);
            WriteHomeLog(message);
        }
        finally
        {
            running = false;
            IsRowSelectionEnabled = true;
            batchCancellation = null;
            BatchProgressPanel.Visibility = Visibility.Collapsed;
            HomeActions.IsEnabled = true;
            RefreshProjects();
            if (closeAfterBatch)
            {
                closeAfterBatch = false;
                Close();
            }
        }
    }

    private static (string ScanRoot, string DisplayRoot) ResolveSiteProfileRoot(ProjectEntry project)
    {
        string rawWslRepo = UnquotePath(ProjectConfig.TryReadString(project.Configuration, "wslRepo"));
        string distribution = ProjectConfig.TryReadString(project.Configuration, "distro");
        if (TryParseWslNetworkPath(rawWslRepo, out var linuxPath, out var namedDistribution))
        {
            rawWslRepo = linuxPath;
            distribution = namedDistribution;
        }

        if (!string.IsNullOrWhiteSpace(rawWslRepo) || !string.IsNullOrWhiteSpace(distribution))
        {
            if (!rawWslRepo.StartsWith('/') || string.IsNullOrWhiteSpace(distribution) ||
                distribution.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
                throw new InvalidOperationException("请先在项目设置中检测并保存本地构建环境。");

            string uncRoot = FileCopy.ResolvePath(rawWslRepo, distribution);
            return (uncRoot, $"{distribution}:{rawWslRepo}");
        }

        string winRepo = project.Repository.Trim();
        if (winRepo.Length > 0)
            return (winRepo, winRepo);

        throw new InvalidOperationException("请先在项目设置中配置本地构建环境（WSL 路径）或 Git 本地仓库目录。");
    }

    private static string[] ExtractConfiguredStringList(string configurationJson, string key, string? legacyKey = null)
    {
        if (string.IsNullOrWhiteSpace(configurationJson))
            return Array.Empty<string>();
        try
        {
            if (JsonNode.Parse(configurationJson) is not JsonObject obj)
                return Array.Empty<string>();

            var result = new List<string>();
            if (obj[key] is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item is JsonValue val && val.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                        result.Add(text.Trim());
                }
            }
            else if (legacyKey is not null && obj[legacyKey] is JsonValue legacyVal &&
                     legacyVal.TryGetValue<string>(out var legacyText) && !string.IsNullOrWhiteSpace(legacyText))
            {
                result.Add(legacyText.Trim());
            }

            return result.ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private void OpenAiSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new AiSettingsWindow(SiteLift.App.App.Store) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.SavedSettings is { } saved)
            {
                string status = saved.IsConfigured
                    ? $"已启用 AI 辅助归档（模型：{saved.Model}）。"
                    : "已保存 AI 助手设置（当前为本地画像规则模式）。";
                HomeStatus.ShowFeedback(status, InfoBarSeverity.Success);
            }
        }
        catch (Exception error)
        {
            HomeStatus.ShowFeedback($"无法打开 AI 助手设置：{error.Message}", InfoBarSeverity.Error);
        }
    }

    private async void ImportMarkdownFromMenu(object sender, RoutedEventArgs e)
    {
        if (running || sender is not System.Windows.Controls.MenuItem { DataContext: ProjectEntry project })
            return;

        var picker = new OpenFileDialog
        {
            Title = $"选择要归档到「{project.Name}」的 Markdown 文档",
            Filter = "Markdown 文档 (*.md;*.mdx;*.markdown)|*.md;*.mdx;*.markdown|所有文件 (*.*)|*.*",
            Multiselect = true
        };
        if (picker.ShowDialog(this) != true || picker.FileNames.Length == 0)
            return;

        await OpenContentIngestFlowAsync(picker.FileNames, project);
    }

    private void HomeWorkspaceDragOver(object sender, DragEventArgs e)
    {
        if (running || savedProjects.Count == 0 || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            HideDropOverlay();
            e.Handled = true;
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || !HasPotentialMarkdownDrop(paths))
        {
            e.Effects = DragDropEffects.None;
            HideDropOverlay();
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        var hoveredProject = FindHoveredProject(e.OriginalSource as DependencyObject);
        DropHintText.Text = hoveredProject is not null
            ? $"松开以归档 Markdown 文档到「{hoveredProject.Name}」"
            : "松开以智能归档 Markdown 文档";
        DropHintOverlay.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void HomeWorkspaceDragLeave(object sender, DragEventArgs e)
    {
        var pos = e.GetPosition(HomeWorkspaceGrid);
        if (pos.X < 0 || pos.Y < 0 || pos.X > HomeWorkspaceGrid.ActualWidth || pos.Y > HomeWorkspaceGrid.ActualHeight)
            HideDropOverlay();
    }

    private async void HomeWorkspaceDrop(object sender, DragEventArgs e)
    {
        HideDropOverlay();
        if (running || savedProjects.Count == 0 || !e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        e.Handled = true;
        try
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] droppedPaths || droppedPaths.Length == 0)
                return;

            var hoveredProject = FindHoveredProject(e.OriginalSource as DependencyObject)
                                 ?? Projects.SelectedItem as ProjectEntry
                                 ?? savedProjects.FirstOrDefault(p => p.Checked)
                                 ?? savedProjects.FirstOrDefault();

            string[] markdownFiles = await Task.Run(() => ContentIngestEngine.DiscoverMarkdownInputs(droppedPaths));
            if (markdownFiles.Length == 0)
            {
                HomeStatus.ShowFeedback("未在拖入的内容中找到 Markdown 文档（.md / .mdx / .markdown）。", InfoBarSeverity.Warning);
                return;
            }

            await OpenContentIngestFlowAsync(markdownFiles, hoveredProject);
        }
        catch (Exception error)
        {
            SiteLift.App.App.AppendDiagnostic(error.ToString());
            HomeStatus.ShowFeedback($"拖入文档处理失败：{error.Message}", InfoBarSeverity.Error);
        }
    }

    private void HideDropOverlay()
    {
        if (DropHintOverlay is not null)
            DropHintOverlay.Visibility = Visibility.Collapsed;
    }

    private static bool HasPotentialMarkdownDrop(string[] paths)
    {
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            string ext = Path.GetExtension(path);
            if (ext.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".mdx", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (Directory.Exists(path))
                return true;
        }
        return false;
    }

    private static ProjectEntry? FindHoveredProject(DependencyObject? source)
    {
        for (var current = source; current is not null; current = GetParentObject(current))
        {
            if (current is DataGridRow { Item: ProjectEntry entry })
                return entry;
        }
        return null;
    }

    private static DependencyObject? GetParentObject(DependencyObject child)
    {
        if (child is Visual or System.Windows.Media.Media3D.Visual3D)
            return VisualTreeHelper.GetParent(child);
        return LogicalTreeHelper.GetParent(child);
    }

    private async Task OpenContentIngestFlowAsync(string[] markdownFiles, ProjectEntry? preferredProject)
    {
        var targets = new List<IngestProjectTarget>();
        foreach (var project in savedProjects)
        {
            try
            {
                var (scanRoot, displayRoot) = ResolveSiteProfileRoot(project);
                targets.Add(new IngestProjectTarget(
                    Id: project.Id,
                    Name: project.Name,
                    ScanRoot: scanRoot,
                    DisplayRoot: displayRoot,
                    Configuration: project.Configuration,
                    PullPaths: ExtractConfiguredStringList(project.Configuration, "pullPaths", "pullPath"),
                    ExcludePaths: ExtractConfiguredStringList(project.Configuration, "exclude")));
            }
            catch
            {
                // Skip projects whose directories are not yet configured.
            }
        }

        if (targets.Count == 0)
        {
            HomeStatus.ShowFeedback("请先为至少一个项目配置本地构建环境（WSL 路径）或 Git 本地仓库目录。", InfoBarSeverity.Warning);
            return;
        }

        var dialog = new ContentIngestWindow(
            SiteLift.App.App.Store,
            targets.ToArray(),
            preferredProject?.Id,
            markdownFiles)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true || dialog.CompletedResults.Count == 0)
            return;

        ResetHomeLog();
        HomeResults.SelectedItem = HomeLogTab;
        SetResultsCollapsed(false);

        int totalAssets = 0;
        WriteHomeLog($"[{dialog.LastIngestedProjectName ?? "站点"}] 文档智能归档");
        foreach (var res in dialog.CompletedResults)
        {
            totalAssets += res.CopiedAssetsCount;
            string overwriteTag = res.OverwroteExisting ? "（覆盖更新）" : "";
            string assetTag = res.CopiedAssetsCount > 0 ? $"，随行复制图片 {res.CopiedAssetsCount} 张" : "";
            string navTag = res.NavigationIndexUpdated && !string.IsNullOrWhiteSpace(res.UpdatedNavigationFile)
                ? $"，已更新导航 {res.UpdatedNavigationFile}"
                : "";
            WriteHomeLog($"已归档：{res.TargetRelativePath}{overwriteTag}{assetTag}{navTag}");
        }

        ProjectEntry? targetEntry = dialog.LastIngestedProjectId is { } targetId
            ? savedProjects.Find(p => p.Id == targetId)
            : null;

        if (targetEntry is not null)
        {
            // Content changed in WSL source repo: invalidate stale build receipt & preview plan.
            InvalidateSuccessfulBuild(targetEntry);
            if (projectRuns.TryGetValue(targetEntry.Id, out var state) && state.Progress != "已提交，待推送")
            {
                projectRuns.Remove(targetEntry.Id);
                DeletePreview(targetEntry);
            }
            RefreshProjects();
        }

        string assetSummary = totalAssets > 0 ? $"，并复制 {totalAssets} 张本地图片" : "";
        HomeStatus.ShowFeedback(
            $"已归档 {dialog.CompletedResults.Count} 篇文档到「{dialog.LastIngestedProjectName}」{assetSummary}。",
            InfoBarSeverity.Success);

        if (dialog.BuildAfterIngest && targetEntry is not null)
        {
            foreach (var p in savedProjects)
                p.Checked = p.Id == targetEntry.Id;
            RefreshProjects();
            await RunSelectedAsync(Operation.Build);
        }
    }

    private bool diagnosingLog;

    private async void DiagnoseHomeLog(object sender, RoutedEventArgs e)
    {
        if (diagnosingLog) return;

        string rawLogs = homeLogBuffer.ToString().Trim();
        if (rawLogs.Length == 0)
        {
            HomeStatus.ShowFeedback("当前运行日志为空，执行构建、同步或推送后再使用报错诊断。", InfoBarSeverity.Informational);
            return;
        }

        var contextProject = Projects.SelectedItem as ProjectEntry
                             ?? savedProjects.FirstOrDefault(p => p.Checked)
                             ?? savedProjects.FirstOrDefault();
        string projectName = contextProject?.Name ?? "SiteLift 站点";
        string framework = contextProject?.FrameworkLabel ?? "通用项目";
        string githubUser = contextProject is not null
            ? ProjectConfig.TryReadString(contextProject.Configuration, "githubUsername")
            : "";
        string sshCmd = contextProject is not null
            ? ProjectConfig.TryReadString(contextProject.Configuration, "gitSshCommand")
            : "";

        string redactedLogs = DiagnosticsRedactor.Redact(rawLogs, githubUser, sshCmd);
        var aiSettings = SiteLift.App.App.Store.LoadAiSettings();

        if (!aiSettings.IsConfigured)
        {
            ShowRedactedPromptCopyDialog(framework, projectName, redactedLogs);
            return;
        }

        diagnosingLog = true;
        DiagnoseHomeLogButton.IsEnabled = false;
        DiagnoseHomeLogButton.Content = "诊断中...";
        try
        {
            var diagnosis = await AiAdvisorClient.DiagnoseFailureAsync(
                aiSettings, framework, projectName, redactedLogs, CancellationToken.None);
            ShowAiDiagnosisDialog(projectName, framework, diagnosis);
        }
        catch (Exception error)
        {
            HomeStatus.ShowFeedback($"AI 诊断失败：{error.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            diagnosingLog = false;
            DiagnoseHomeLogButton.IsEnabled = true;
            DiagnoseHomeLogButton.Content = "AI 诊断";
        }
    }

    private void ShowRedactedPromptCopyDialog(string framework, string projectName, string redactedLogs)
    {
        string prompt = AiAdvisorClient.BuildDiagnosisCopyPrompt(framework, projectName, redactedLogs);

        var body = new System.Windows.Controls.DockPanel { Margin = new Thickness(20, 16, 20, 18) };
        var dialog = FluentDialog.Create(this, "复制脱敏排错上下文", body, 560);

        var hint = new System.Windows.Controls.TextBlock
        {
            Text = "尚未启用 AI 接口。已自动对用户名、Token 与 SSH 参数脱敏，你可以一键复制给任意 AI 工具排查，或先配置 AI 接口。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        hint.SetResourceReference(FrameworkElement.StyleProperty, "SettingsCardSubtitle");
        System.Windows.Controls.DockPanel.SetDock(hint, System.Windows.Controls.Dock.Top);
        body.Children.Add(hint);

        var footerActions = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var footer = new System.Windows.Controls.Border
        {
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(0, 12, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = footerActions
        };
        footer.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        System.Windows.Controls.DockPanel.SetDock(footer, System.Windows.Controls.Dock.Bottom);
        body.Children.Add(footer);

        var closeBtn = new Wpf.Ui.Controls.Button { Content = "关闭", IsCancel = true, Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary, Margin = new Thickness(0, 0, 8, 0) };
        closeBtn.SetResourceReference(FrameworkElement.StyleProperty, "WorkspaceTextButton");
        var configBtn = new Wpf.Ui.Controls.Button { Content = "配置 AI 接口", Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary, Margin = new Thickness(0, 0, 8, 0) };
        configBtn.SetResourceReference(FrameworkElement.StyleProperty, "WorkspaceTextButton");
        var copyBtn = new Wpf.Ui.Controls.Button { Content = "复制排错 Prompt", IsDefault = true, Appearance = Wpf.Ui.Controls.ControlAppearance.Primary };
        copyBtn.SetResourceReference(FrameworkElement.StyleProperty, "WorkspaceTextButton");

        bool openSettings = false;
        copyBtn.Click += (_, _) =>
        {
            Clipboard.SetText(prompt);
            HomeStatus.ShowFeedback("已复制脱敏后的排错上下文到剪贴板。", InfoBarSeverity.Success);
            dialog.Close();
        };
        configBtn.Click += (_, _) =>
        {
            openSettings = true;
            dialog.Close();
        };
        footerActions.Children.Add(closeBtn);
        footerActions.Children.Add(configBtn);
        footerActions.Children.Add(copyBtn);

        var previewBox = new Wpf.Ui.Controls.TextBox
        {
            Text = prompt,
            IsReadOnly = true,
            AcceptsReturn = true,
            Height = 240,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto
        };
        previewBox.SetResourceReference(FrameworkElement.StyleProperty, "WorkspaceLog");
        body.Children.Add(previewBox);

        dialog.ShowDialog();
        if (openSettings)
            OpenAiSettings(this, new RoutedEventArgs());
    }

    private void ShowAiDiagnosisDialog(string projectName, string framework, AiDiagnosisResult diagnosis)
    {
        var body = new System.Windows.Controls.DockPanel { Margin = new Thickness(20, 16, 20, 18) };
        var dialog = FluentDialog.Create(this, $"AI 报错诊断 · {projectName}", body, 580);

        var footerActions = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var footer = new System.Windows.Controls.Border
        {
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(0, 12, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = footerActions
        };
        footer.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        System.Windows.Controls.DockPanel.SetDock(footer, System.Windows.Controls.Dock.Bottom);
        body.Children.Add(footer);

        if (diagnosis.SuggestedCommands.Length > 0)
        {
            var copyCmdBtn = new Wpf.Ui.Controls.Button
            {
                Content = "复制建议命令",
                Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
                Margin = new Thickness(0, 0, 8, 0)
            };
            copyCmdBtn.SetResourceReference(FrameworkElement.StyleProperty, "WorkspaceTextButton");
            copyCmdBtn.Click += (_, _) =>
            {
                Clipboard.SetText(string.Join(Environment.NewLine, diagnosis.SuggestedCommands));
                HomeStatus.ShowFeedback("已复制建议命令到剪贴板（请确认后再在终端执行）。", InfoBarSeverity.Success);
            };
            footerActions.Children.Add(copyCmdBtn);
        }

        var okBtn = new Wpf.Ui.Controls.Button { Content = "知道了", IsDefault = true, IsCancel = true, Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, MinWidth = 88 };
        okBtn.SetResourceReference(FrameworkElement.StyleProperty, "WorkspaceTextButton");
        footerActions.Children.Add(okBtn);

        var contentStack = new System.Windows.Controls.StackPanel();
        var summaryBlock = new System.Windows.Controls.TextBlock
        {
            Text = diagnosis.Summary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        };
        summaryBlock.SetResourceReference(FrameworkElement.StyleProperty, "SettingsGroupHeader");
        contentStack.Children.Add(summaryBlock);

        var metaBlock = new System.Windows.Controls.TextBlock
        {
            Text = $"架构：{framework} · 仅提供修复建议，不会自动执行命令",
            Margin = new Thickness(0, 0, 0, 10)
        };
        metaBlock.SetResourceReference(FrameworkElement.StyleProperty, "SettingsCardSubtitle");
        contentStack.Children.Add(metaBlock);

        if (!string.IsNullOrWhiteSpace(diagnosis.RootCause))
        {
            var causeHeader = new System.Windows.Controls.TextBlock { Text = "原因分析", Margin = new Thickness(0, 0, 0, 4) };
            causeHeader.SetResourceReference(FrameworkElement.StyleProperty, "SettingsGroupHeader");
            contentStack.Children.Add(causeHeader);

            var causeText = new System.Windows.Controls.TextBlock
            {
                Text = diagnosis.RootCause,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
                Margin = new Thickness(0, 0, 0, 12)
            };
            contentStack.Children.Add(causeText);
        }

        if (diagnosis.FixSteps.Length > 0)
        {
            var stepsHeader = new System.Windows.Controls.TextBlock { Text = "建议修复步骤", Margin = new Thickness(0, 0, 0, 4) };
            stepsHeader.SetResourceReference(FrameworkElement.StyleProperty, "SettingsGroupHeader");
            contentStack.Children.Add(stepsHeader);

            var numbered = string.Join(Environment.NewLine, diagnosis.FixSteps.Select((s, i) => $"{i + 1}. {s}"));
            var stepsText = new System.Windows.Controls.TextBlock
            {
                Text = numbered,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 22,
                Margin = new Thickness(0, 0, 0, 12)
            };
            contentStack.Children.Add(stepsText);
        }

        if (diagnosis.SuggestedCommands.Length > 0)
        {
            var cmdHeader = new System.Windows.Controls.TextBlock { Text = "参考命令（请确认后手动执行）", Margin = new Thickness(0, 0, 0, 4) };
            cmdHeader.SetResourceReference(FrameworkElement.StyleProperty, "SettingsGroupHeader");
            contentStack.Children.Add(cmdHeader);

            var cmdBox = new Wpf.Ui.Controls.TextBox
            {
                Text = string.Join(Environment.NewLine, diagnosis.SuggestedCommands),
                IsReadOnly = true,
                AcceptsReturn = true,
                MaxHeight = 110,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto
            };
            cmdBox.SetResourceReference(FrameworkElement.StyleProperty, "WorkspaceLog");
            contentStack.Children.Add(cmdBox);
        }

        var scroller = new System.Windows.Controls.ScrollViewer
        {
            MaxHeight = 420,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
            Content = contentStack
        };
        body.Children.Add(scroller);
        dialog.ShowDialog();
    }
}
