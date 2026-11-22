using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SiteLift.Core;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace SiteLift.App.Views;

public partial class MainWindow
{
    private static bool HasGitMetadata(string path)
    {
        string metadata = Path.Combine(path, ".git");
        return Directory.Exists(metadata) || File.Exists(metadata);
    }

    private void UpdateLocalGitAction()
    {
        string path = windowsRepo.Text.Trim();
        LocalGitAction.IsEnabled = Path.IsPathFullyQualified(path) && Directory.Exists(path);
        LocalGitAction.Content = LocalGitAction.IsEnabled && HasGitMetadata(path) ? "重置 Git 数据" : "连接 Git 仓库";
    }

    private async void CheckWindowsGit(object sender, RoutedEventArgs e)
    {
        if (running || detectingWsl) return;
        running = true;
        SettingsTabs.IsEnabled = Actions.IsEnabled = false;
        WindowsGitStatus.ShowFeedback("正在检测 Windows Git…");
        try
        {
            string version = await SyncEngine.CheckWindowsGitAsync();
            WindowsGitStatus.ShowFeedback($"已检测到 Git（{version}），可以继续。", InfoBarSeverity.Success);
        }
        catch (SyncException error) when (error.Code == SyncErrorCode.WindowsGitMissing)
        {
            ShowWindowsGitMissing(WindowsGitStatus, error.Message);
        }
        catch (Exception error) { WindowsGitStatus.ShowFeedback(error.Message, InfoBarSeverity.Error); }
        finally
        {
            running = false;
            SettingsTabs.IsEnabled = Actions.IsEnabled = true;
        }
    }

    private async void ConfigureLocalRepository(object sender, RoutedEventArgs e)
    {
        if (running || detectingWsl || jsonDraft)
            return;

        string path = windowsRepo.Text.Trim();
        if (!Directory.Exists(path) || !Path.IsPathFullyQualified(path))
        {
            LocalGitStatus.ShowFeedback("请选择现有的 Windows 项目目录。", InfoBarSeverity.Warning);
            return;
        }

        bool reset;
        bool hasFiles;
        try
        {
            reset = HasGitMetadata(path);
            using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            hasFiles = entries.MoveNext();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            LocalGitStatus.ShowFeedback($"无法读取本地目录：{error.Message}", InfoBarSeverity.Error);
            return;
        }

        string address = remoteUrl.Text.Trim();
        string targetBranch = branch.Text.Trim();
        if (address.Length == 0 || targetBranch.Length == 0)
        {
            SettingsTabs.SelectedItem = RemoteSettingsTab;
            RemoteDetectionStatus.ShowFeedback("请填写远端仓库地址和目标分支，再回到「Git 本地仓库」继续。", InfoBarSeverity.Warning);
            return;
        }

        string operation = reset ? "重置 Git 数据" : "连接 Git 仓库";
        string confirmation = reset
            ? "按所选远端分支重建 Git 元数据，保留工作目录中的文件。原有本地提交、分支、暂存区和仓库设置会保存在原 .git 的备份中。"
            : hasFiles
            ? "接入所选远端分支的历史，保留现有文件。完成后将显示本地差异。"
            : "将所选远端分支克隆到此空目录。";
        if (FluentDialog.Ask(this, operation, $"{confirmation}\n\n本地目录：{path}\n远端：{address}\n目标分支：{targetBranch}",
            reset ? "重置" : "连接", "取消", destructive: reset) != MessageBoxResult.Yes)
            return;

        running = true;
        SettingsTabs.IsEnabled = Actions.IsEnabled = false;
        Status.IsOpen = false;
        LocalGitStatus.ShowFeedback(reset ? "正在重置 Git 数据…" : "正在连接 Git 仓库…");
        var messages = new List<string>();
        try
        {
            var config = ProjectConfig.Parse(SerializeConfig());
            var mode = reset ? RepositoryInitMode.Reset
                : hasFiles ? RepositoryInitMode.Existing
                : RepositoryInitMode.Empty;
            var outcome = await SyncEngine.InitializeRepositoryAsync(config, mode,
                line => messages.Add(line));

            InvalidateLocalRepositoryPlans(path);
            string result = reset ? $"Git 数据已重置，工作文件已保留。原 .git 备份：{outcome.BackupPath}" :
                hasFiles ? "Git 仓库已连接，现有文件已保留。" : "克隆完成，可以构建。";
            if (outcome.StatusUnavailable)
                result += "\n无法读取差异。请在本地仓库查看 Git 状态。";
            LocalGitStatus.ShowFeedback(result, outcome.StatusUnavailable ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            if (hasFiles && !outcome.StatusUnavailable)
                ShowInitializedChanges(outcome.Changes);
        }
        catch (SyncException error) when (error.Code == SyncErrorCode.WindowsGitMissing)
        {
            ShowWindowsGitMissing(LocalGitStatus, error.Message);
        }
        catch (Exception error)
        {
            string details = error is SyncException || messages.Count == 0
                ? error.Message : string.Join(Environment.NewLine, messages);
            LocalGitStatus.ShowFeedback($"{operation}未完成。{Environment.NewLine}{details}", InfoBarSeverity.Error);
        }
        finally
        {
            running = false;
            SettingsTabs.IsEnabled = Actions.IsEnabled = true;
            UpdateLocalGitAction();
        }
    }

    private void InvalidateLocalRepositoryPlans(string path)
    {
        string repository = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        foreach (var project in savedProjects)
        {
            if (string.IsNullOrWhiteSpace(project.Repository) || !Path.IsPathFullyQualified(project.Repository))
                continue;

            string savedPath;
            try { savedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.Repository)); }
            catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
            {
                continue;
            }
            if (!string.Equals(repository, savedPath, StringComparison.OrdinalIgnoreCase))
                continue;

            projectRuns.Remove(project.Id);
            DeletePreview(project);
            SiteLift.App.App.Store.DeletePendingPush(project.Id);
        }
        ShowSavedChanges();
        RefreshProjects();
    }

    private void ShowInitializedChanges(IReadOnlyList<string> changes)
    {
        var close = new Wpf.Ui.Controls.Button { Content = "关闭", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(close, Dock.Bottom);
        panel.Children.Add(close);
        panel.Children.Add(new Wpf.Ui.Controls.TextBox
        {
            IsReadOnly = true,
            Text = changes.Count == 0 ? "当前文件与远端基线一致。" : string.Join(Environment.NewLine, changes),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 10)
        });
        var dialog = FluentDialog.Create(this, "Git 仓库 · 本地差异", panel, 680, 350);
        dialog.ShowDialog();
    }
}
