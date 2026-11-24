using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using SiteLift.Core;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SiteLift.App.Views;

internal sealed class UpdateDialog
{
    private readonly Window owner;
    private readonly Version currentVersion;
    private readonly ReleaseInfo release;
    private readonly InstallDirectoryStatus installStatus;
    private readonly HttpClient downloadClient;
    private readonly string updatesDirectory;
    private readonly Action<string> openReleasesPage;
    private readonly Action<StagedUpdate, InstallDirectoryStatus> launchApply;

    private FluentWindow? dialog;
    private TextBlock? headlineText;
    private TextBlock? metaText;
    private TextBlock? detailText;
    private StackPanel? progressPanel;
    private ProgressRing? stageRing;
    private TextBlock? progressText;
    private ProgressBar? progressBar;
    private InfoBar? errorBar;
    private WrapPanel? actionsPanel;

    private CancellationTokenSource? downloadCts;
    private bool busy;
    private bool applying;
    private bool closing;
    private bool updateLaunched;

    private UpdateDialog(
        Window owner,
        Version currentVersion,
        ReleaseInfo release,
        InstallDirectoryStatus installStatus,
        HttpClient downloadClient,
        string updatesDirectory,
        Action<string> openReleasesPage,
        Action<StagedUpdate, InstallDirectoryStatus> launchApply)
    {
        this.owner = owner;
        this.currentVersion = AppUpdater.NormalizeThreePartVersion(currentVersion);
        this.release = release;
        this.installStatus = installStatus;
        this.downloadClient = downloadClient;
        this.updatesDirectory = updatesDirectory;
        this.openReleasesPage = openReleasesPage;
        this.launchApply = launchApply;
    }

    internal static bool Show(
        Window owner,
        Version currentVersion,
        ReleaseInfo release,
        InstallDirectoryStatus installStatus,
        HttpClient downloadClient,
        string updatesDirectory,
        Action<string> openReleasesPage,
        Action<StagedUpdate, InstallDirectoryStatus> launchApply)
    {
        var controller = new UpdateDialog(
            owner,
            currentVersion,
            release,
            installStatus,
            downloadClient,
            updatesDirectory,
            openReleasesPage,
            launchApply);
        return controller.Run();
    }

    private bool Run()
    {
        var panel = new DockPanel { Margin = new Thickness(20, 16, 20, 18) };

        actionsPanel = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var footer = new Border
        {
            Margin = new Thickness(0, 20, 0, 0),
            Child = actionsPanel
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        panel.Children.Add(footer);

        var body = new StackPanel();

        headlineText = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            LineHeight = 20,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextWrapping = TextWrapping.Wrap
        };
        Typography.SetNumeralAlignment(headlineText, FontNumeralAlignment.Tabular);
        body.Children.Add(headlineText);

        metaText = new TextBlock
        {
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 12,
            LineHeight = 18,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextWrapping = TextWrapping.Wrap
        };
        metaText.SetResourceReference(TextBlock.FontFamilyProperty, "WorkspaceSmallTypeface");
        metaText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Typography.SetNumeralAlignment(metaText, FontNumeralAlignment.Tabular);
        body.Children.Add(metaText);

        detailText = new TextBlock
        {
            Margin = new Thickness(0, 12, 0, 0),
            LineHeight = 20,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextWrapping = TextWrapping.Wrap
        };
        detailText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        body.Children.Add(detailText);

        progressPanel = new StackPanel
        {
            Margin = new Thickness(0, 14, 0, 0),
            Visibility = Visibility.Collapsed
        };

        var stageRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8)
        };
        stageRing = new ProgressRing
        {
            Width = 16,
            Height = 16,
            IsIndeterminate = true,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        stageRow.Children.Add(stageRing);

        progressText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            LineHeight = 18,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextWrapping = TextWrapping.Wrap
        };
        progressText.SetResourceReference(TextBlock.FontFamilyProperty, "WorkspaceSmallTypeface");
        progressText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Typography.SetNumeralAlignment(progressText, FontNumeralAlignment.Tabular);
        stageRow.Children.Add(progressText);
        progressPanel.Children.Add(stageRow);

        progressBar = new ProgressBar
        {
            Height = 4,
            Minimum = 0,
            Maximum = 100,
            Value = 0
        };
        progressPanel.Children.Add(progressBar);
        body.Children.Add(progressPanel);

        errorBar = new InfoBar
        {
            Margin = new Thickness(0, 12, 0, 0),
            Severity = InfoBarSeverity.Error,
            IsClosable = false,
            IsOpen = false
        };
        body.Children.Add(errorBar);

        panel.Children.Add(body);

        dialog = FluentDialog.Create(owner, "发现新版本", panel, 460);
        dialog.Closing += (_, args) =>
        {
            if (applying)
            {
                args.Cancel = true;
                return;
            }
            closing = true;
            if (busy)
                downloadCts?.Cancel();
        };

        ShowConfirmState();
        dialog.ShowDialog();
        downloadCts?.Dispose();
        return updateLaunched;
    }

    private void ShowConfirmState()
    {
        if (headlineText is null || metaText is null || detailText is null ||
            progressPanel is null || errorBar is null || actionsPanel is null)
            return;

        string target = release.Version.ToString(3);
        headlineText.Text = $"发现新版本 v{target}";

        string sizeSummary = release.PackageAsset is { SizeBytes: > 0 } asset
            ? $" · 安装包约 {AppUpdater.FormatBytes(asset.SizeBytes)}"
            : "";
        metaText.Text = $"当前版本 v{currentVersion} → 新版本 v{target}{sizeSummary}";

        string elevationNote = installStatus.RequiresElevation
            ? "当前程序目录需要管理员权限，应用更新时会弹出系统权限确认。"
            : "";
        detailText.Text = $"将在应用内下载更新包并完成 SHA-256 校验，随后自动替换程序文件并重启。{elevationNote}";
        detailText.Visibility = Visibility.Visible;
        progressPanel.Visibility = Visibility.Collapsed;
        errorBar.IsOpen = false;

        actionsPanel.Children.Clear();
        AddAction("取消", ControlAppearance.Secondary, isCancel: true, isDefault: false, () => dialog?.Close());
        AddAction("打开发布页", ControlAppearance.Secondary, isCancel: false, isDefault: false, () =>
        {
            dialog?.Close();
            openReleasesPage(release.ReleaseUrl);
        });
        var updateButton = AddAction("立即更新", ControlAppearance.Primary, isCancel: false, isDefault: true, () => _ = BeginUpdateAsync());
        if (dialog is not null)
            dialog.Loaded += (_, _) => updateButton.Focus();
    }

    private void ShowDownloadingState()
    {
        if (headlineText is null || detailText is null || progressPanel is null ||
            stageRing is null || progressText is null || progressBar is null ||
            errorBar is null || actionsPanel is null)
            return;

        headlineText.Text = $"正在更新到 v{release.Version.ToString(3)}";
        detailText.Visibility = Visibility.Collapsed;
        errorBar.IsOpen = false;
        progressPanel.Visibility = Visibility.Visible;
        stageRing.Visibility = Visibility.Visible;
        progressBar.IsIndeterminate = true;
        progressBar.Value = 0;
        progressText.Text = "正在获取校验信息…";

        actionsPanel.Children.Clear();
        AddAction("取消", ControlAppearance.Secondary, isCancel: true, isDefault: false, () =>
        {
            downloadCts?.Cancel();
        });
    }

    private void ShowErrorState(string message)
    {
        if (headlineText is null || detailText is null || progressPanel is null ||
            errorBar is null || actionsPanel is null)
            return;

        headlineText.Text = $"更新到 v{release.Version.ToString(3)} 未完成";
        detailText.Visibility = Visibility.Collapsed;
        progressPanel.Visibility = Visibility.Collapsed;
        errorBar.Message = message;
        errorBar.IsOpen = true;

        actionsPanel.Children.Clear();
        AddAction("关闭", ControlAppearance.Secondary, isCancel: true, isDefault: false, () => dialog?.Close());
        AddAction("打开发布页", ControlAppearance.Secondary, isCancel: false, isDefault: false, () =>
        {
            dialog?.Close();
            openReleasesPage(release.ReleaseUrl);
        });
        var retry = AddAction("重试", ControlAppearance.Primary, isCancel: false, isDefault: true, () => _ = BeginUpdateAsync());
        retry.Focus();
    }

    private void UpdateProgressUi(UpdateProgress progress)
    {
        if (progressText is null || progressBar is null || stageRing is null)
            return;

        switch (progress.Stage)
        {
            case UpdateStage.DownloadingChecksum:
                stageRing.Visibility = Visibility.Visible;
                progressBar.IsIndeterminate = true;
                progressText.Text = "正在获取校验文件…";
                break;

            case UpdateStage.DownloadingPackage:
                stageRing.Visibility = Visibility.Collapsed;
                if (progress.Percentage is double percent && progress.TotalBytes is > 0)
                {
                    progressBar.IsIndeterminate = false;
                    progressBar.Value = percent;
                    progressText.Text = $"正在下载更新包… {AppUpdater.FormatBytes(progress.BytesReceived)} / {AppUpdater.FormatBytes(progress.TotalBytes.Value)} ({percent:F0}%)";
                }
                else
                {
                    progressBar.IsIndeterminate = true;
                    progressText.Text = $"正在下载更新包… {AppUpdater.FormatBytes(progress.BytesReceived)}";
                }
                break;

            case UpdateStage.VerifyingChecksum:
                stageRing.Visibility = Visibility.Visible;
                progressBar.IsIndeterminate = true;
                progressText.Text = "正在校验文件完整性 (SHA-256)…";
                break;

            case UpdateStage.ExtractingPackage:
                stageRing.Visibility = Visibility.Visible;
                progressBar.IsIndeterminate = true;
                progressText.Text = "正在解压并核验程序文件…";
                break;

            case UpdateStage.PreparingApply:
                stageRing.Visibility = Visibility.Visible;
                progressBar.IsIndeterminate = true;
                progressText.Text = installStatus.RequiresElevation
                    ? "正在请求权限并准备重启更新…"
                    : "正在准备应用更新并重启…";
                break;
        }
    }

    private async Task BeginUpdateAsync()
    {
        if (busy)
            return;

        busy = true;
        downloadCts?.Dispose();
        downloadCts = new CancellationTokenSource();
        var token = downloadCts.Token;

        ShowDownloadingState();

        var progress = new Progress<UpdateProgress>(UpdateProgressUi);
        StagedUpdate? staged = null;
        try
        {
            staged = await AppUpdater.DownloadAndStageAsync(
                downloadClient,
                release,
                updatesDirectory,
                currentVersion,
                progress,
                token);

            token.ThrowIfCancellationRequested();

            applying = true;
            if (actionsPanel is not null)
                actionsPanel.IsEnabled = false;
            UpdateProgressUi(new UpdateProgress(UpdateStage.PreparingApply, 0, null));

            launchApply(staged, installStatus);

            updateLaunched = true;
            applying = false;
            busy = false;
            dialog?.Close();
        }
        catch (OperationCanceledException canceled)
        {
            applying = false;
            busy = false;
            if (actionsPanel is not null)
                actionsPanel.IsEnabled = true;

            if (staged is not null)
            {
                AppUpdater.TryDeleteDirectory(staged.StagingRoot);
                if (!closing)
                    ShowErrorState(string.IsNullOrWhiteSpace(canceled.Message) ? "已取消应用更新。" : canceled.Message);
            }
            else if (!closing && dialog?.IsVisible == true)
            {
                dialog.Close();
            }
        }
        catch (Exception error) when (error is UpdateException or HttpRequestException or System.IO.IOException)
        {
            applying = false;
            busy = false;
            if (actionsPanel is not null)
                actionsPanel.IsEnabled = true;

            if (staged is not null)
                AppUpdater.TryDeleteDirectory(staged.StagingRoot);

            SiteLift.App.App.AppendDiagnostic($"In-place update failed: {error}");
            if (!closing)
            {
                string message = error is UpdateException
                    ? error.Message
                    : $"下载更新包失败：{error.Message}";
                ShowErrorState(message);
            }
        }
    }

    private Button AddAction(string label, ControlAppearance appearance, bool isCancel, bool isDefault, Action onClick)
    {
        var button = new Button
        {
            Content = label,
            Appearance = appearance,
            IsCancel = isCancel,
            IsDefault = isDefault,
            MinWidth = 84,
            Margin = new Thickness(8, 0, 0, 0)
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "WorkspaceTextButton");
        button.Click += (_, _) => onClick();
        actionsPanel!.Children.Add(button);
        return button;
    }
}
