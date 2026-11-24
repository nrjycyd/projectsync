using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SiteLift.Core;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace SiteLift.App.Views;

public partial class MainWindow
{
    private static readonly Version CurrentVersion = typeof(MainWindow).Assembly.GetName().Version!;
    private static readonly Version CurrentThreePartVersion = AppUpdater.NormalizeThreePartVersion(CurrentVersion);
    private static readonly HttpClient ReleaseClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly HttpClient UpdateDownloadClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    private ReleaseInfo? discoveredRelease;
    private CancellationTokenSource? silentUpdateCheckCts;
    private bool checkingManualUpdates;
    private bool startupUpdatesInitialized;

    private void InitializeUpdatesOnStartup()
    {
        if (startupUpdatesInitialized)
            return;
        startupUpdatesInitialized = true;

        _ = ProcessPreviousUpdateOutcomeAsync();
        _ = RunSilentStartupUpdateCheckAsync();
    }

    private void CancelStartupUpdateCheck()
    {
        silentUpdateCheckCts?.Cancel();
        silentUpdateCheckCts?.Dispose();
        silentUpdateCheckCts = null;
    }

    private async Task ProcessPreviousUpdateOutcomeAsync()
    {
        UpdateOutcome? outcome;
        try
        {
            outcome = await Task.Run(() => AppUpdater.ConsumeUpdateOutcomeAndCleanup(SiteLift.App.App.Store.UpdatesDirectory));
        }
        catch (Exception error)
        {
            SiteLift.App.App.AppendDiagnostic($"ConsumeUpdateOutcome failed: {error}");
            return;
        }

        if (outcome is null || !IsLoaded)
            return;

        string currentText = CurrentThreePartVersion.ToString(3);
        if (outcome.Succeeded && string.Equals(outcome.TargetVersion, currentText, StringComparison.Ordinal))
        {
            HomeStatus.ShowFeedback($"已更新至 v{outcome.TargetVersion}。", InfoBarSeverity.Success);
        }
        else if (!outcome.Succeeded)
        {
            string reason = string.IsNullOrWhiteSpace(outcome.Message) ? "" : $"：{outcome.Message}";
            HomeStatus.ShowFeedback($"上次更新至 v{outcome.TargetVersion} 未完成，已恢复原版本{reason}（详情见诊断日志）。", InfoBarSeverity.Warning);
        }
    }

    private async Task RunSilentStartupUpdateCheckAsync()
    {
        CancelStartupUpdateCheck();
        var cts = new CancellationTokenSource();
        silentUpdateCheckCts = cts;
        try
        {
            var result = await FetchLatestReleaseAsync(cts.Token);
            if (cts.IsCancellationRequested || checkingManualUpdates || !IsLoaded)
                return;

            if (result.Release is { } release && release.Version > CurrentThreePartVersion)
            {
                discoveredRelease = release;
                ApplyFooterUpdateHint(release);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            SiteLift.App.App.AppendDiagnostic($"Silent update check skipped: {error.Message}");
        }
    }

    private void ApplyFooterUpdateHint(ReleaseInfo? release)
    {
        if (release is not null && release.Version > CurrentThreePartVersion)
        {
            string target = release.Version.ToString(3);
            UpdateAvailableDot.Visibility = Visibility.Visible;
            UpdateActionText.Text = $"发现新版 v{target}";
            UpdateActionText.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            AppVersionText.Visibility = Visibility.Collapsed;
            CheckUpdatesButton.ToolTip = $"当前版本 v{CurrentThreePartVersion}，点击更新到 v{target}";
        }
        else
        {
            UpdateAvailableDot.Visibility = Visibility.Collapsed;
            UpdateActionText.Text = "检查更新";
            UpdateActionText.ClearValue(TextBlock.ForegroundProperty);
            AppVersionText.Visibility = Visibility.Visible;
            CheckUpdatesButton.ToolTip = null;
        }
    }

    private async void CheckUpdates(object sender, RoutedEventArgs e)
    {
        if (checkingManualUpdates)
            return;

        if (running || detectingWsl)
        {
            HomeStatus.ShowFeedback("当前有任务正在执行，请等待完成后再更新。", InfoBarSeverity.Informational);
            return;
        }

        if (discoveredRelease is { } cached && cached.Version > CurrentThreePartVersion)
        {
            PresentAvailableRelease(cached);
            return;
        }

        CancelStartupUpdateCheck();
        checkingManualUpdates = true;
        CheckUpdatesButton.IsEnabled = false;
        UpdateAvailableDot.Visibility = Visibility.Collapsed;
        UpdateActionText.ClearValue(TextBlock.ForegroundProperty);
        UpdateActionText.Text = "检查中…";

        try
        {
            var result = await FetchLatestReleaseAsync(CancellationToken.None);
            if (result.NotFound)
            {
                OfferReleasesPage("GitHub 没有返回可公开读取的版本。是否在浏览器打开发布页？");
                return;
            }

            if (result.Release is null)
            {
                OfferReleasesPage("无法识别最新版本号。是否在浏览器打开发布页？");
                return;
            }

            if (result.Release.Version > CurrentThreePartVersion)
            {
                discoveredRelease = result.Release;
                ApplyFooterUpdateHint(result.Release);
                PresentAvailableRelease(result.Release);
            }
            else
            {
                discoveredRelease = null;
                ApplyFooterUpdateHint(null);
                FluentDialog.Ask(this, "检查更新", $"当前版本 v{CurrentThreePartVersion} 已是最新。", "关闭");
            }
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException)
        {
            OfferReleasesPage("暂时无法检查更新。是否在浏览器打开发布页？");
        }
        finally
        {
            checkingManualUpdates = false;
            ApplyFooterUpdateHint(discoveredRelease);
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private void PresentAvailableRelease(ReleaseInfo release)
    {
        if (running || detectingWsl)
        {
            HomeStatus.ShowFeedback("当前有任务正在执行，请等待完成后再更新。", InfoBarSeverity.Informational);
            return;
        }

        string target = release.Version.ToString(3);
        if (!release.CanInPlaceUpdate)
        {
            OfferReleasesPage($"发现新版 v{target}，但发布页缺少自动更新包。是否打开 GitHub 发布页？", release.ReleaseUrl);
            return;
        }

        var installStatus = AppUpdater.InspectInstallDirectory(AppContext.BaseDirectory);
        if (!installStatus.CanInPlaceUpdate)
        {
            OfferReleasesPage($"发现新版 v{target}。{installStatus.BlockedReason}是否打开 GitHub 发布页？", release.ReleaseUrl);
            return;
        }

        bool updateLaunched = UpdateDialog.Show(
            this,
            CurrentThreePartVersion,
            release,
            installStatus,
            UpdateDownloadClient,
            SiteLift.App.App.Store.UpdatesDirectory,
            OpenReleasesPageUrl,
            LaunchStagedUpdate);

        if (updateLaunched)
        {
            ForceStopAllPreviews();
            Application.Current.Shutdown();
        }
    }

    private void LaunchStagedUpdate(StagedUpdate staged, InstallDirectoryStatus installStatus)
    {
        if (running || detectingWsl || !CanLeaveProject())
            throw new UpdateException("当前存在未完成的操作或未保存的更改，请处理后再更新。");

        var (scriptPath, planPath) = AppUpdater.PrepareApplyScript(
            staged,
            installStatus.DirectoryPath,
            SiteLift.App.App.Store.UpdatesDirectory,
            SiteLift.App.App.DiagnosticsDirectory,
            Environment.ProcessId,
            CurrentThreePartVersion,
            installStatus.RequiresElevation);

        AppUpdater.LaunchUpdater(scriptPath, planPath, installStatus.RequiresElevation);
    }

    private static async Task<(ReleaseInfo? Release, bool NotFound)> FetchLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AppUpdater.LatestReleaseApi);
        request.Headers.UserAgent.ParseAdd($"SiteLift/{CurrentThreePartVersion}");
        using var response = await ReleaseClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return (null, NotFound: true);

        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        return (AppUpdater.ParseLatestRelease(json), NotFound: false);
    }

    private void OfferReleasesPage(string message, string? url = null)
    {
        if (FluentDialog.Ask(this, "检查更新", message, "打开", "取消") != MessageBoxResult.Yes)
            return;

        OpenReleasesPageUrl(url ?? AppUpdater.ReleasesUrl);
    }

    private void OpenReleasesPageUrl(string url)
    {
        string targetUrl = string.IsNullOrWhiteSpace(url) ? AppUpdater.ReleasesUrl : url;
        try
        {
            using var browser = Process.Start(new ProcessStartInfo(targetUrl) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            FluentDialog.Ask(this, "检查更新", $"无法打开浏览器，请访问 {targetUrl}", "关闭");
        }
    }
}
