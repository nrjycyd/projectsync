using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SiteLift.Core;
using ControlAppearance = Wpf.Ui.Controls.ControlAppearance;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace SiteLift.App.Views;

public sealed record IngestProjectTarget(
    Guid Id,
    string Name,
    string ScanRoot,
    string DisplayRoot,
    string Configuration,
    string[] PullPaths,
    string[] ExcludePaths);

public partial class ContentIngestWindow : Wpf.Ui.Controls.FluentWindow
{
    private sealed record DirectoryChoice(string Path, string DisplayText);
    private sealed record AssetPreviewItem(string RoutePreview, string LinkPreview);

    private readonly AppDataStore store;
    private readonly IngestProjectTarget[] projects;
    private readonly string[] sourceFiles;
    private readonly List<string> remainingFiles;

    private IngestProjectTarget? currentProject;
    private DroppedDocumentInfo? currentDocument;
    private SiteProfileDocument? currentProfile;
    private ContentIngestPlan? currentPlan;
    private string? initialRecommendedDirectory;
    private CancellationTokenSource? planCts;
    private bool suppressingUiEvents;
    private bool busy;

    public Guid? LastIngestedProjectId { get; private set; }
    public string? LastIngestedProjectName { get; private set; }
    public bool BuildAfterIngest { get; private set; }
    public List<ContentIngestResult> CompletedResults { get; } = new();

    public ContentIngestWindow(
        AppDataStore store,
        IngestProjectTarget[] projects,
        Guid? initialProjectId,
        string[] sourceFiles)
    {
        this.store = store;
        this.projects = projects;
        this.sourceFiles = sourceFiles;
        remainingFiles = sourceFiles.ToList();

        InitializeComponent();

        suppressingUiEvents = true;
        ProjectSelectorCombo.ItemsSource = projects;
        int initialIndex = initialProjectId.HasValue
            ? Array.FindIndex(projects, p => p.Id == initialProjectId.Value)
            : 0;
        ProjectSelectorCombo.SelectedIndex = initialIndex >= 0 ? initialIndex : 0;
        currentProject = ProjectSelectorCombo.SelectedItem as IngestProjectTarget;

        RefreshFileSelector();
        suppressingUiEvents = false;

        Loaded += async (_, _) => await LoadAndPlanCurrentAsync();
        Closed += (_, _) =>
        {
            planCts?.Cancel();
            planCts?.Dispose();
        };
    }

    private void RefreshFileSelector()
    {
        if (remainingFiles.Count > 1)
        {
            MultiFileBar.Visibility = Visibility.Visible;
            MultiFileCountText.Content = $"已拖入 {remainingFiles.Count} 篇文档，当前处理：";
            FileSelectorCombo.ItemsSource = remainingFiles.Select(Path.GetFileName).ToArray();
            FileSelectorCombo.SelectedIndex = 0;
        }
        else
        {
            MultiFileBar.Visibility = Visibility.Collapsed;
            FileSelectorCombo.ItemsSource = null;
        }
    }

    private string? CurrentSourceFilePath()
    {
        if (remainingFiles.Count == 0) return null;
        int idx = MultiFileBar.Visibility == Visibility.Visible && FileSelectorCombo.SelectedIndex >= 0
            ? FileSelectorCombo.SelectedIndex
            : 0;
        return idx < remainingFiles.Count ? remainingFiles[idx] : remainingFiles[0];
    }

    private async Task LoadAndPlanCurrentAsync()
    {
        string? filePath = CurrentSourceFilePath();
        if (filePath is null || currentProject is null)
            return;

        planCts?.Cancel();
        planCts?.Dispose();
        planCts = new CancellationTokenSource();
        var token = planCts.Token;

        SetPlanningBusy(true);
        IngestStatusBar.IsOpen = false;
        var project = currentProject;

        try
        {
            var document = await Task.Run(() => ContentIngestEngine.InspectDroppedDocument(filePath), token);
            currentDocument = document;
            DocTitleText.Text = document.DetectedTitle;
            DocSourcePathText.Text = document.SourceFilePath;

            var scanOptions = new SiteProfileScanOptions(
                ConfigurationJson: project.Configuration,
                PullPaths: project.PullPaths,
                ExcludePaths: project.ExcludePaths,
                ProjectName: project.Name,
                EnsureGitExclude: true);

            var (profile, autoScanned) = await Task.Run(
                () => ContentIngestEngine.LoadOrScanProfile(project.ScanRoot, scanOptions, token),
                token);
            currentProfile = profile;

            string themeLabel = string.Equals(profile.Theme, "default", StringComparison.OrdinalIgnoreCase)
                ? profile.Framework
                : $"{profile.Framework} · {profile.Theme}";
            SiteFrameworkBadge.Content = themeLabel;
            ProfileStatusText.Text = autoScanned
                ? $"未发现画像，已自动扫描生成 .sitelift/profile.json（{profile.AutoScanned.Directories.Length} 个目录）"
                : $"已读取 .sitelift/profile.json（{profile.AutoScanned.Directories.Length} 个目录，{profile.AutoScanned.Taxonomy.Tags.Length} 个标签）";

            if (!autoScanned)
            {
                var freshness = await Task.Run(
                    () => SiteProfileScanner.CheckStructureFreshness(project.ScanRoot, profile, scanOptions, token),
                    token);
                if (freshness.IsStale)
                {
                    StaleProfileInfoBar.Message = freshness.Reason;
                    StaleProfilePanel.Visibility = Visibility.Visible;
                }
                else
                {
                    StaleProfilePanel.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                StaleProfilePanel.Visibility = Visibility.Collapsed;
            }

            var heuristic = await Task.Run(
                () => ContentIngestEngine.BuildHeuristicPlan(profile, project.ScanRoot, document),
                token);

            ApplyPlanToUi(heuristic);

            // If AI is configured and enabled, enhance asynchronously while keeping heuristic visible immediately.
            var aiSettings = store.LoadAiSettings();
            if (aiSettings.IsConfigured)
            {
                AiProgressRing.Visibility = Visibility.Visible;
                RouteModeBadge.Content = "AI 分析中...";
                RouteModeBadge.Appearance = ControlAppearance.Info;
                try
                {
                    var aiSuggestion = await AiAdvisorClient.RequestRoutingAsync(
                        aiSettings, profile, document, heuristic.AllowedDirectories, token);
                    token.ThrowIfCancellationRequested();

                    var merged = ContentIngestEngine.MergeAiSuggestion(
                        heuristic, aiSuggestion, profile, project.ScanRoot, document);
                    ApplyPlanToUi(merged);
                    if (!string.IsNullOrWhiteSpace(merged.WarningNote))
                        IngestStatusBar.ShowFeedback(merged.WarningNote!, InfoBarSeverity.Warning);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception aiError)
                {
                    RouteModeBadge.Content = "本地规则推荐";
                    RouteModeBadge.Appearance = ControlAppearance.Secondary;
                    IngestStatusBar.ShowFeedback($"AI 分析失败，已回退至本地画像规则：{aiError.Message}", InfoBarSeverity.Warning);
                }
                finally
                {
                    AiProgressRing.Visibility = Visibility.Collapsed;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error)
        {
            IngestStatusBar.ShowFeedback($"无法准备归档方案：{error.Message}", InfoBarSeverity.Error);
            currentPlan = null;
            UpdateActionButtons();
        }
        finally
        {
            SetPlanningBusy(false);
        }
    }

    private void ApplyPlanToUi(ContentIngestPlan plan)
    {
        currentPlan = plan;
        initialRecommendedDirectory = plan.TargetDirectory;
        suppressingUiEvents = true;
        try
        {
            RememberHintPanel.Visibility = Visibility.Collapsed;
            RememberHintCheck.IsChecked = false;
            RoutingHintBox.Text = "";
            if (plan.SourceMode == "ai")
            {
                RouteModeBadge.Content = "AI 智能推荐";
                RouteModeBadge.Appearance = ControlAppearance.Success;
            }
            else
            {
                RouteModeBadge.Content = "本地规则推荐";
                RouteModeBadge.Appearance = ControlAppearance.Secondary;
            }

            // Populate TargetDirCombo: top candidates first (marked 推荐), then remaining allowed directories
            var choices = new List<DirectoryChoice>();
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string DescribeDir(string path, bool isTopRecommended, bool isCandidate)
            {
                var info = currentProfile?.AutoScanned.Directories
                    .FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
                string titlePart = info is not null && !string.IsNullOrWhiteSpace(info.Title) &&
                                   !string.Equals(info.Title, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)
                    ? $" — {info.Title}"
                    : "";
                string countPart = info is not null ? $" ({info.ArticleCount} 篇)" : "";
                string tag = isTopRecommended ? " ★ 推荐" : isCandidate ? " · 备选" : "";
                return $"{path}{titlePart}{countPart}{tag}";
            }

            for (int i = 0; i < plan.CandidateDirectories.Length; i++)
            {
                string cand = plan.CandidateDirectories[i];
                if (added.Add(cand))
                    choices.Add(new DirectoryChoice(cand, DescribeDir(cand, i == 0, i > 0)));
            }
            foreach (string allowed in plan.AllowedDirectories)
            {
                if (added.Add(allowed))
                    choices.Add(new DirectoryChoice(allowed, DescribeDir(allowed, false, false)));
            }

            TargetDirCombo.ItemsSource = choices;
            int selectedIdx = choices.FindIndex(c => string.Equals(c.Path, plan.TargetDirectory, StringComparison.OrdinalIgnoreCase));
            TargetDirCombo.SelectedIndex = selectedIdx >= 0 ? selectedIdx : 0;

            RouteReasonText.Text = $"推荐理由：{plan.Reason}";
            TargetFileNameBox.Text = plan.SuggestedFileName;

            ApplyFrontmatterCheck.IsChecked = plan.ShouldInjectFrontmatter;
            FrontmatterEditor.Text = plan.SuggestedFrontmatter;
            FrontmatterEditor.IsEnabled = plan.ShouldInjectFrontmatter;
            FrontmatterHintText.Text = currentDocument?.HasFrontmatter == true
                ? "原文档已含 Frontmatter，默认保留；勾选可覆盖为下方内容"
                : string.IsNullOrWhiteSpace(plan.SuggestedFrontmatter)
                    ? "当前架构无需 Frontmatter"
                    : $"格式：{currentProfile?.AutoScanned.Frontmatter.Format ?? "yaml"}（可直接编辑）";

            UpdateDynamicPreviewFromPlan(plan);
        }
        finally
        {
            suppressingUiEvents = false;
        }
        UpdateActionButtons();
    }

    private void UpdateDynamicPreviewFromPlan(ContentIngestPlan plan)
    {
        string displayRoot = currentProject?.DisplayRoot ?? "";
        TargetFullPreviewText.Text = $"写入目标：{displayRoot}/{plan.TargetRelativePath}";

        if (plan.WillOverwriteExisting)
        {
            OverwriteWarningPanel.Visibility = Visibility.Visible;
            OverwriteInfoBar.Message = $"目标路径已存在「{plan.TargetRelativePath}」，请修改文件名或勾选覆盖确认。";
            AllowOverwriteCheck.IsChecked = false;
        }
        else
        {
            OverwriteWarningPanel.Visibility = Visibility.Collapsed;
            AllowOverwriteCheck.IsChecked = false;
        }

        if (plan.NavigationPatch is { CanAutoApply: true, AlreadyPresent: false } autoPatch)
        {
            NavigationPatchPanel.Visibility = Visibility.Visible;
            ApplyNavPatchCheck.Content = $"同步追加导航条目到 {autoPatch.IndexFileRelativePath}";
            ApplyNavPatchCheck.IsChecked = true;
            NavigationPatchPreviewBox.Text = autoPatch.DiffPreview;
            NavigationAdvicePanel.Visibility = Visibility.Collapsed;
        }
        else if (plan.NavigationPatch is { AlreadyPresent: true } existingPatch)
        {
            NavigationPatchPanel.Visibility = Visibility.Collapsed;
            NavigationAdvicePanel.Visibility = Visibility.Visible;
            NavigationInfoBar.Message = $"导航文件 {existingPatch.IndexFileRelativePath} 已包含该文档条目，无需重复追加。";
        }
        else if (plan.NavigationPatch is { CanAutoApply: false } manualPatch)
        {
            NavigationPatchPanel.Visibility = Visibility.Collapsed;
            NavigationAdvicePanel.Visibility = Visibility.Visible;
            NavigationInfoBar.Message = manualPatch.LineToAppend;
        }
        else if (!string.IsNullOrWhiteSpace(plan.NavigationAdvice))
        {
            NavigationPatchPanel.Visibility = Visibility.Collapsed;
            NavigationAdvicePanel.Visibility = Visibility.Visible;
            NavigationInfoBar.Message = plan.NavigationAdvice;
        }
        else
        {
            NavigationPatchPanel.Visibility = Visibility.Collapsed;
            NavigationAdvicePanel.Visibility = Visibility.Collapsed;
        }

        if (plan.AssetTransfers.Length > 0)
        {
            CopyAssetsCheck.IsEnabled = true;
            CopyAssetsCheck.IsChecked = true;
            AssetsSummaryText.Text = $"检测到 {plan.AssetTransfers.Length} 张本地图片";
            AssetsList.ItemsSource = plan.AssetTransfers
                .Select(a => new AssetPreviewItem(
                    $"{Path.GetFileName(a.SourcePath)} → {a.DestinationRelativePath}",
                    $"链接改为 {a.RewrittenMarkdownRef}"))
                .ToArray();
            AssetsList.Visibility = Visibility.Visible;
        }
        else
        {
            CopyAssetsCheck.IsEnabled = false;
            CopyAssetsCheck.IsChecked = false;
            AssetsSummaryText.Text = "未检测到需要搬运的本地图片";
            AssetsList.ItemsSource = null;
            AssetsList.Visibility = Visibility.Collapsed;
        }

        if (plan.MissingAssetRefs.Length > 0)
        {
            MissingAssetsWarningText.Visibility = Visibility.Visible;
            MissingAssetsWarningText.Text = $"提示：有 {plan.MissingAssetRefs.Length} 处图片引用在原目录未找到本地文件（将保持原链接）：{string.Join("、", plan.MissingAssetRefs.Take(4))}";
        }
        else
        {
            MissingAssetsWarningText.Visibility = Visibility.Collapsed;
        }
    }

    private void RebuildForManualEdit()
    {
        if (suppressingUiEvents || currentPlan is null || currentProfile is null || currentProject is null || currentDocument is null)
            return;
        if (TargetDirCombo.SelectedItem is not DirectoryChoice choice)
            return;

        string enteredName = TargetFileNameBox.Text.Trim();
        if (enteredName.Length == 0)
        {
            UpdateActionButtons();
            return;
        }

        try
        {
            currentPlan = ContentIngestEngine.RebuildPlanForSelection(
                currentPlan, currentProfile, currentProject.ScanRoot, currentDocument, choice.Path, enteredName);
            UpdateDynamicPreviewFromPlan(currentPlan);

            bool changedDir = !string.IsNullOrWhiteSpace(initialRecommendedDirectory) &&
                              !string.Equals(choice.Path, initialRecommendedDirectory, StringComparison.OrdinalIgnoreCase);
            if (changedDir)
            {
                if (RememberHintPanel.Visibility != Visibility.Visible)
                {
                    RememberHintPanel.Visibility = Visibility.Visible;
                    RoutingHintBox.Text = $"关于「{currentDocument.DetectedTitle}」这类文章放入 {choice.Path}";
                }
            }
            else
            {
                RememberHintPanel.Visibility = Visibility.Collapsed;
                RememberHintCheck.IsChecked = false;
            }

            IngestStatusBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            IngestStatusBar.ShowFeedback(ex.Message, InfoBarSeverity.Warning);
        }
        UpdateActionButtons();
    }

    private void SetPlanningBusy(bool isBusy)
    {
        busy = isBusy;
        BusyProgressRing.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        ProjectSelectorCombo.IsEnabled = !isBusy;
        FileSelectorCombo.IsEnabled = !isBusy;
        ReanalyzeButton.IsEnabled = !isBusy;
        RefreshProfileButton.IsEnabled = !isBusy;
        BatchArchiveAllButton.IsEnabled = !isBusy && remainingFiles.Count > 1;
        UpdateActionButtons();
    }

    private void UpdateActionButtons()
    {
        bool hasValidTarget = !busy &&
                              currentPlan is not null &&
                              currentProject is not null &&
                              currentDocument is not null &&
                              TargetDirCombo.SelectedItem is DirectoryChoice &&
                              !string.IsNullOrWhiteSpace(TargetFileNameBox.Text) &&
                              (!currentPlan.WillOverwriteExisting || AllowOverwriteCheck.IsChecked == true);

        ArchiveOnlyButton.IsEnabled = hasValidTarget;
        ArchiveAndBuildButton.IsEnabled = hasValidTarget;
    }

    private async void ProjectSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressingUiEvents) return;
        currentProject = ProjectSelectorCombo.SelectedItem as IngestProjectTarget;
        await LoadAndPlanCurrentAsync();
    }

    private async void FileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressingUiEvents) return;
        await LoadAndPlanCurrentAsync();
    }

    private void TargetSelectionChanged(object sender, SelectionChangedEventArgs e) => RebuildForManualEdit();

    private void TargetFileNameChanged(object sender, TextChangedEventArgs e) => RebuildForManualEdit();

    private void OverwriteCheckChanged(object sender, RoutedEventArgs e) => UpdateActionButtons();

    private void FrontmatterCheckChanged(object sender, RoutedEventArgs e)
    {
        FrontmatterEditor.IsEnabled = ApplyFrontmatterCheck.IsChecked == true;
    }

    private async void ReanalyzeClick(object sender, RoutedEventArgs e) => await LoadAndPlanCurrentAsync();

    private async void RefreshProfileClick(object sender, RoutedEventArgs e)
    {
        if (busy || currentProject is null) return;
        SetPlanningBusy(true);
        try
        {
            var project = currentProject;
            var scanOptions = new SiteProfileScanOptions(
                ConfigurationJson: project.Configuration,
                PullPaths: project.PullPaths,
                ExcludePaths: project.ExcludePaths,
                ProjectName: project.Name,
                EnsureGitExclude: true);
            await Task.Run(() => SiteProfileScanner.ScanAndSave(project.ScanRoot, scanOptions));
            StaleProfilePanel.Visibility = Visibility.Collapsed;
        }
        catch (Exception error)
        {
            IngestStatusBar.ShowFeedback($"刷新画像失败：{error.Message}", InfoBarSeverity.Error);
            SetPlanningBusy(false);
            return;
        }
        await LoadAndPlanCurrentAsync();
    }

    private async void BatchArchiveAllClick(object sender, RoutedEventArgs e)
    {
        if (busy || currentProject is null || currentProfile is null || remainingFiles.Count <= 1)
            return;

        SetPlanningBusy(true);
        var project = currentProject;
        var profile = currentProfile;
        var aiSettings = store.LoadAiSettings();
        int succeeded = 0;
        int skippedConflicts = 0;
        int failed = 0;
        var completedPaths = new List<string>();

        try
        {
            foreach (string filePath in remainingFiles.ToArray())
            {
                try
                {
                    var doc = await Task.Run(() => ContentIngestEngine.InspectDroppedDocument(filePath));
                    var plan = await Task.Run(() => ContentIngestEngine.BuildHeuristicPlan(profile, project.ScanRoot, doc));
                    if (aiSettings.IsConfigured)
                    {
                        try
                        {
                            var ai = await AiAdvisorClient.RequestRoutingAsync(
                                aiSettings, profile, doc, plan.AllowedDirectories, CancellationToken.None);
                            plan = ContentIngestEngine.MergeAiSuggestion(plan, ai, profile, project.ScanRoot, doc);
                        }
                        catch { /* fall back to heuristic plan cleanly */ }
                    }

                    if (plan.WillOverwriteExisting)
                    {
                        skippedConflicts++;
                        continue;
                    }

                    var req = new ContentIngestRequest(
                        TargetDirectory: plan.TargetDirectory,
                        TargetFileName: plan.SuggestedFileName,
                        ApplyFrontmatter: plan.ShouldInjectFrontmatter,
                        FrontmatterText: plan.SuggestedFrontmatter,
                        CopyLocalAssets: plan.AssetTransfers.Length > 0,
                        AllowOverwrite: false,
                        AssetTransfers: plan.AssetTransfers,
                        ApplyNavigationPatch: plan.NavigationPatch is { CanAutoApply: true, AlreadyPresent: false },
                        NavigationPatch: plan.NavigationPatch);

                    var res = await Task.Run(() => ContentIngestEngine.ApplyIngest(project.ScanRoot, doc, req));
                    CompletedResults.Add(res);
                    LastIngestedProjectId = project.Id;
                    LastIngestedProjectName = project.Name;
                    completedPaths.Add(filePath);
                    succeeded++;
                }
                catch
                {
                    failed++;
                }
            }

            foreach (string done in completedPaths)
                remainingFiles.Remove(done);

            if (remainingFiles.Count == 0 && succeeded > 0)
            {
                BuildAfterIngest = false;
                DialogResult = true;
                Close();
                return;
            }

            suppressingUiEvents = true;
            RefreshFileSelector();
            suppressingUiEvents = false;
            IngestStatusBar.ShowFeedback(
                $"批量归档完成：成功 {succeeded} 篇，跳过同名冲突 {skippedConflicts} 篇{(failed > 0 ? $"，失败 {failed} 篇" : "")}。剩余文档请逐一确认。",
                succeeded > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
            if (remainingFiles.Count > 0)
                await LoadAndPlanCurrentAsync();
        }
        finally
        {
            SetPlanningBusy(false);
        }
    }

    private async void OpenAiSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new AiSettingsWindow(store) { Owner = this };
        if (dialog.ShowDialog() == true)
            await LoadAndPlanCurrentAsync();
    }

    private void CopyNavAdviceClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(NavigationInfoBar.Message))
        {
            Clipboard.SetText(NavigationInfoBar.Message);
            IngestStatusBar.ShowFeedback("已复制导航提示到剪贴板。", InfoBarSeverity.Success);
        }
    }

    private async void ArchiveOnlyClick(object sender, RoutedEventArgs e) => await ExecuteIngestAsync(buildAfter: false);

    private async void ArchiveAndBuildClick(object sender, RoutedEventArgs e) => await ExecuteIngestAsync(buildAfter: true);

    private async Task ExecuteIngestAsync(bool buildAfter)
    {
        if (busy || currentPlan is null || currentProject is null || currentDocument is null)
            return;
        if (TargetDirCombo.SelectedItem is not DirectoryChoice choice)
            return;

        string targetFileName = TargetFileNameBox.Text.Trim();
        var request = new ContentIngestRequest(
            TargetDirectory: choice.Path,
            TargetFileName: targetFileName,
            ApplyFrontmatter: ApplyFrontmatterCheck.IsChecked == true,
            FrontmatterText: FrontmatterEditor.Text,
            CopyLocalAssets: CopyAssetsCheck.IsChecked == true,
            AllowOverwrite: AllowOverwriteCheck.IsChecked == true,
            AssetTransfers: currentPlan.AssetTransfers,
            ApplyNavigationPatch: NavigationPatchPanel.Visibility == Visibility.Visible && ApplyNavPatchCheck.IsChecked == true,
            NavigationPatch: currentPlan.NavigationPatch);

        SetPlanningBusy(true);
        try
        {
            var project = currentProject;
            var document = currentDocument;
            var result = await Task.Run(() => ContentIngestEngine.ApplyIngest(project.ScanRoot, document, request));

            if (RememberHintPanel.Visibility == Visibility.Visible &&
                RememberHintCheck.IsChecked == true &&
                !string.IsNullOrWhiteSpace(RoutingHintBox.Text))
            {
                string hintText = RoutingHintBox.Text.Trim();
                await Task.Run(() => SiteProfileScanner.AppendUserRoutingHint(project.ScanRoot, hintText));
            }

            CompletedResults.Add(result);
            LastIngestedProjectId = project.Id;
            LastIngestedProjectName = project.Name;

            // Remove completed file from queue
            remainingFiles.Remove(document.SourceFilePath);
            if (remainingFiles.Count > 0 && !buildAfter)
            {
                suppressingUiEvents = true;
                RefreshFileSelector();
                suppressingUiEvents = false;
                IngestStatusBar.ShowFeedback(
                    $"已归档「{result.TargetRelativePath}」（图片复制 {result.CopiedAssetsCount} 张），请继续确认下一篇文档。",
                    InfoBarSeverity.Success);
                await LoadAndPlanCurrentAsync();
                return;
            }

            BuildAfterIngest = buildAfter;
            DialogResult = true;
            Close();
        }
        catch (Exception error)
        {
            IngestStatusBar.ShowFeedback($"归档失败：{error.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SetPlanningBusy(false);
        }
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = CompletedResults.Count > 0;
        Close();
    }
}
