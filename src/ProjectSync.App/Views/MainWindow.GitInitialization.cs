using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace ProjectSync.App.Views;

public partial class MainWindow
{
    private async void InitializeLocalGit(object sender, RoutedEventArgs e)
    {
        if (running || detectingWsl || jsonDraft)
            return;

        string path = windowsRepo.Text.Trim();
        if (!Directory.Exists(path) || !Path.IsPathFullyQualified(path))
        {
            LocalGitStatus.ShowFeedback("请选择现有的 Windows 项目目录。", InfoBarSeverity.Warning);
            return;
        }

        bool hasFiles;
        try
        {
            var metadata = Path.Combine(path, ".git");
            if (Directory.Exists(metadata) || File.Exists(metadata))
            {
                LocalGitStatus.ShowFeedback("此目录已有 Git 元数据。如仓库不可用，请检查现有仓库。");
                return;
            }
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
            RemoteDetectionStatus.ShowFeedback("请填写远端仓库地址和目标分支，再回到「Git 本地仓库」初始化。", InfoBarSeverity.Warning);
            return;
        }

        string confirmation = hasFiles
            ? "接入所选远端分支的历史，保留现有文件。完成后将显示本地差异。"
            : "将所选远端分支克隆到此空目录。";
        if (FluentDialog.Ask(this, "初始化 Git", $"{confirmation}\n\n本地目录：{path}\n远端：{address}\n目标分支：{targetBranch}",
            "确定", "取消") != MessageBoxResult.Yes)
            return;

        running = true;
        SettingsTabs.IsEnabled = Actions.IsEnabled = false;
        Status.IsOpen = false;
        LocalGitStatus.ShowFeedback("正在初始化 Git。如弹出浏览器，请完成登录。");
        var changes = new List<string>();
        var messages = new List<string>();
        bool completed = false;
        bool statusUnavailable = false;
        try
        {
            await RunProjectAsync(SerializeConfig(), false, false, null, line =>
            {
                if (line.StartsWith("LOCAL_CHANGE=", StringComparison.Ordinal))
                    changes.Add(line["LOCAL_CHANGE=".Length..]);
                else if (line == "LOCAL_INIT=complete")
                    completed = true;
                else if (line == "LOCAL_STATUS=unavailable")
                    statusUnavailable = true;
                else
                    messages.Add(line);
            }, initializeRepository: hasFiles ? "existing" : "empty");
            if (!completed)
                throw new InvalidOperationException("未收到 Git 初始化完成状态。");

            if (hasFiles && statusUnavailable)
                LocalGitStatus.ShowFeedback("Git 已初始化，但无法读取差异。请在本地仓库查看 Git 状态。", InfoBarSeverity.Warning);
            else
                LocalGitStatus.ShowFeedback(hasFiles ? "Git 已初始化，现有文件已保留。" : "克隆完成，可以构建。", InfoBarSeverity.Success);
            if (hasFiles && !statusUnavailable)
                ShowInitializedChanges(changes);
        }
        catch (Exception error)
        {
            string details = messages.Count == 0 ? error.Message : string.Join(Environment.NewLine, messages);
            LocalGitStatus.ShowFeedback($"Git 初始化未完成。{Environment.NewLine}{details}", InfoBarSeverity.Error);
        }
        finally
        {
            running = false;
            SettingsTabs.IsEnabled = Actions.IsEnabled = true;
        }
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
        var dialog = FluentDialog.Create(this, "初始化完成 · 本地差异", panel, 680, 350);
        dialog.ShowDialog();
    }
}
