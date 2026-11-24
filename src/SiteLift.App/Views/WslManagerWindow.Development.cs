using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SiteLift.Core;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace SiteLift.App.Views;

public partial class WslManagerWindow
{
    private void ConfigureDevelopmentClick(object sender, RoutedEventArgs e)
    {
        if (busy || Row(sender) is not { } instance) return;
        var (dialog, form, footer) = CreateDialog("配置开发环境", 580);
        form.Children.Add(CreateDialogBanner(
            $"实例：{instance.Name}",
            "适用于 APT 发行版，在 WSL 终端运行，按提示输入 sudo 密码"));

        var user = new Wpf.Ui.Controls.TextBox
        {
            Width = 200,
            MinHeight = 34,
            PlaceholderText = "默认用户"
        };
        System.Windows.Automation.AutomationProperties.SetName(user, "Linux 用户（留空使用默认用户）");
        form.Children.Add(new Wpf.Ui.Controls.CardControl
        {
            Margin = new Thickness(0, 0, 0, 12),
            Padding = new Thickness(14, 12, 14, 12),
            IsTabStop = false,
            Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Settings24 },
            Header = CreateCardHeader("Linux 用户（可选）", "留空将使用该发行版的默认用户执行安装"),
            Content = user
        });

        var componentsHeading = new TextBlock
        {
            Text = "开发组件与版本",
            Margin = new Thickness(0, 2, 0, 8)
        };
        componentsHeading.SetResourceReference(FrameworkElement.StyleProperty, "SettingsGroupHeader");
        form.Children.Add(componentsHeading);

        bool darkTheme = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() == Wpf.Ui.Appearance.ApplicationTheme.Dark;
        var git = ComponentCard(form, "Git", "通过 APT 安装版本控制工具", "git.png", "latest", null, "latest");

        var alias = new Wpf.Ui.Controls.ToggleSwitch
        {
            OnContent = "同时提供 python 命令（python-is-python3）",
            OffContent = "同时提供 python 命令（python-is-python3）",
            Margin = new Thickness(32, 10, 0, 0)
        };
        var python = ComponentCard(form, "Python 3", "通过 APT 安装 Python 运行时", "python3.png", "latest", alias, "latest");

        var downloadNotice = new Wpf.Ui.Controls.InfoBar
        {
            IsOpen = false, IsClosable = false, Margin = new Thickness(0, 10, 0, 0)
        };
        var node = ComponentCard(form, "Node.js（nvm）", "通过 nvm 管理多版本 Node.js", "node.png", "lts/*", downloadNotice, "latest", "lts/*");

        bool closed = false;
        int probeVersion = 0;
        var dialogCts = new CancellationTokenSource();
        async Task CheckDownloadToolAsync()
        {
            int request = ++probeVersion;
            if (!node.Selected)
            {
                downloadNotice.IsOpen = false;
                return;
            }
            downloadNotice.ShowFeedback("正在检查 Node.js 下载工具…");
            try
            {
                var args = new List<string> { "-d", instance.Name };
                string login = user.Text.Trim();
                if (login.Length > 0) args.AddRange(new[] { "-u", login });
                args.AddRange(new[] { "--exec", "bash", "-c",
                    "if command -v curl >/dev/null || command -v wget >/dev/null; then echo available; else echo missing; fi" });
                var result = await InvokeAsync(args.ToArray(), encoding: System.Text.Encoding.UTF8,
                    timeout: TimeSpan.FromSeconds(10), cancellation: dialogCts.Token);
                if (closed || request != probeVersion) return;
                if (result.Code == 0 && result.Output.Trim() == "available")
                    downloadNotice.IsOpen = false;
                else if (result.Code == 0 && result.Output.Trim() == "missing")
                    downloadNotice.ShowFeedback("缺少下载工具。继续安装时将补装 curl 和 CA 证书。", InfoBarSeverity.Warning);
                else
                    downloadNotice.ShowFeedback("暂无法检测下载工具，安装前会重新检查。", InfoBarSeverity.Warning);
            }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException or InvalidOperationException or
                System.ComponentModel.Win32Exception or System.IO.IOException)
            {
                if (!closed && request == probeVersion)
                    downloadNotice.ShowFeedback("暂无法检测下载工具，安装前会重新检查。", InfoBarSeverity.Warning);
            }
        }

        dialog.Closed += (_, _) =>
        {
            closed = true;
            try { dialogCts.Cancel(); } catch (ObjectDisposedException) { }
        };
        dialog.Loaded += async (_, _) => await CheckDownloadToolAsync();
        node.Check.Checked += async (_, _) => await CheckDownloadToolAsync();
        node.Check.Unchecked += async (_, _) => await CheckDownloadToolAsync();
        user.LostKeyboardFocus += async (_, _) => { if (!closed) await CheckDownloadToolAsync(); };
        var pnpm = ComponentCard(form, "pnpm", "需要 nvm 管理的 Node.js", darkTheme ? "pnpm-dark.png" : "pnpm.png", "latest", null, "latest");

        var versionStatus = new Wpf.Ui.Controls.InfoBar { IsClosable = false, Margin = new Thickness(0, 6, 0, 0) };
        versionStatus.ShowFeedback("正在读取可用版本…");
        form.Children.Add(versionStatus);
        dialog.Loaded += async (_, _) =>
        {
            try
            {
                await LoadDevelopmentVersionsAsync(instance.Name, git, python, node, pnpm, versionStatus, dialogCts.Token);
            }
            finally
            {
                if (closed) dialogCts.Dispose();
            }
        };

        bool Valid()
        {
            string login = user.Text.Trim();
            return (login.Length == 0 || Regex.IsMatch(login, @"\A[A-Za-z_][A-Za-z0-9_.-]*[$]?\z")) &&
                (git.Selected || python.Selected || node.Selected || pnpm.Selected || alias.IsChecked == true) &&
                ValidApt(git) && ValidApt(python) &&
                (!node.Selected || Regex.IsMatch(node.Version, @"\A(?:latest|lts/\*|v?[0-9]+(?:\.[0-9]+){0,2})\z")) &&
                (!pnpm.Selected || pnpm.Version == "latest" || Regex.IsMatch(pnpm.Version, @"\A[0-9]+(?:\.[0-9]+){0,2}(?:-[A-Za-z0-9.-]+)?\z"));
        }
        bool accepted = Confirm(dialog, footer, "在终端安装", Valid, "请至少勾选一项，并检查用户名及版本格式。");
        dialogCts.Dispose();
        if (!accepted) return;

        try
        {
            string[] packages = new[]
            {
                git.Selected ? Package("git", git.Version) : "",
                python.Selected ? Package("python3", python.Version) : "",
                alias.IsChecked == true ? "python-is-python3" : ""
            }.Where(package => package.Length > 0).ToArray();
            var start = new ProcessStartInfo(ProcessRunner.WslExecutablePath) { UseShellExecute = true };
            start.ArgumentList.Add("-d");
            start.ArgumentList.Add(instance.Name);
            if (!string.IsNullOrWhiteSpace(user.Text))
            {
                start.ArgumentList.Add("-u");
                start.ArgumentList.Add(user.Text.Trim());
            }
            string script = DevelopmentSetupScript(packages.Length > 0, node.Selected, pnpm.Selected);
            foreach (string argument in new[] { "--cd", "~", "--exec", "bash", "-c", script, "sitelift-setup",
                node.Version == "latest" ? "node" : node.Version, pnpm.Version })
                start.ArgumentList.Add(argument);
            foreach (string package in packages) start.ArgumentList.Add(package);
            using var terminal = Process.Start(start) ?? throw new InvalidOperationException("无法启动 WSL 终端。");
            Status.ShowFeedback("安装进度和版本验证结果显示在 WSL 终端中。");
        }
        catch (Exception error) { Status.ShowFeedback(error.Message, InfoBarSeverity.Error); }
    }

    private sealed record Component(Wpf.Ui.Controls.ToggleSwitch Check, ComboBox Input)
    {
        internal bool Selected => Check.IsChecked == true;
        internal string Version => (string?)Input.SelectedItem ?? "";
    }

    private static Component ComponentCard(StackPanel form, string label, string subtitle, string iconFile,
        string version, UIElement? extraContent, params string[] choices)
    {
        var icon = new Image
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(
                new Uri($"pack://application:,,,/SiteLift.App;component/Assets/DevelopmentTools/{iconFile}", UriKind.Absolute)),
            Width = 20,
            Height = 20,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var input = new ComboBox
        {
            ItemsSource = choices,
            SelectedItem = version,
            Width = 172,
            MinHeight = 32,
            MaxDropDownHeight = 250,
            VerticalAlignment = VerticalAlignment.Center
        };
        System.Windows.Automation.AutomationProperties.SetName(input, label + "版本");

        var check = new Wpf.Ui.Controls.ToggleSwitch
        {
            IsChecked = true,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        System.Windows.Automation.AutomationProperties.SetName(check, "安装 " + label);
        check.Checked += (_, _) => input.IsEnabled = true;
        check.Unchecked += (_, _) => input.IsEnabled = false;

        var rightControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        rightControls.Children.Add(input);
        rightControls.Children.Add(check);

        var row = new DockPanel();
        DockPanel.SetDock(rightControls, Dock.Right);
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(rightControls);
        row.Children.Add(icon);
        row.Children.Add(CreateCardHeader(label, subtitle));

        var content = new StackPanel();
        content.Children.Add(row);
        if (extraContent is not null)
            content.Children.Add(extraContent);

        form.Children.Add(CreateDialogCard(content, new Thickness(14, 10, 14, 10), new Thickness(0, 0, 0, 8)));
        return new Component(check, input);
    }

    private static bool ValidApt(Component component) => !component.Selected || component.Version == "latest" ||
        Regex.IsMatch(component.Version, @"\A[0-9][A-Za-z0-9.+:~\-]*\z");

    private static string Package(string name, string version) => version == "latest" ? name : $"{name}={version}";

    private static readonly HttpClient VersionClient = new() { Timeout = TimeSpan.FromSeconds(12) };

    private async Task LoadDevelopmentVersionsAsync(string instance, Component git, Component python,
        Component node, Component pnpm, Wpf.Ui.Controls.InfoBar status, CancellationToken token)
    {
        async Task<bool> Fill(Component component, Func<Task<string[]>> read)
        {
            try
            {
                string[] versions = await read();
                if (token.IsCancellationRequested || versions.Length == 0) return false;
                string selected = component.Version;
                component.Input.ItemsSource = component.Input.Items.Cast<string>().Concat(versions).Distinct().ToArray();
                component.Input.SelectedItem = selected;
                return true;
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or TimeoutException or JsonException or System.ComponentModel.Win32Exception or System.IO.IOException or InvalidOperationException or KeyNotFoundException)
            {
                return false;
            }
        }

        Task<string[]> Apt(string package) => ReadAptVersionsAsync(instance, package, token);
        bool[] loaded = await Task.WhenAll(
            Fill(git, () => Apt("git")), Fill(python, () => Apt("python3")),
            Fill(node, () => ReadPublishedVersionsAsync(true, token)),
            Fill(pnpm, () => ReadPublishedVersionsAsync(false, token)));
        if (token.IsCancellationRequested) return;
        string[] names = { "Git", "Python 3", "Node.js", "pnpm" };
        var missing = names.Where((_, index) => !loaded[index]).ToArray();
        if (missing.Length == 0)
            status.IsOpen = false;
        else
            status.ShowFeedback($"未能读取具体版本：{string.Join("、", missing)}。仍可使用默认选项。", InfoBarSeverity.Warning);
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
        using var request = new HttpRequestMessage(HttpMethod.Get, node ?
            "https://nodejs.org/dist/index.json" : "https://registry.npmjs.org/pnpm");
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

    private static string DevelopmentSetupScript(bool apt, bool node, bool pnpm)
    {
        var commands = new List<string>
        {
            """trap 'status=$?; printf "\n安装退出代码：%s\n按 Enter 关闭终端…" "$status"; read -r _ || true; exit "$status"' EXIT""",
            "set -e -o pipefail",
            """node_version=$1; pnpm_version=$2; shift 2""",
            """apt_install() { command -v apt-get >/dev/null || { echo "当前发行版没有 APT。"; return 1; }; if [[ $EUID == 0 ]]; then apt-get update && apt-get install -y -- "$@"; else sudo apt-get update && sudo apt-get install -y -- "$@"; fi; }"""
        };
        if (apt)
            commands.Add("""apt_install "$@"; for package in "$@"; do case "${package%%=*}" in git) git --version;; python3) python3 --version;; python-is-python3) python --version;; esac; done""");
        if (node || pnpm)
        {
            commands.Add("""export NVM_DIR="${NVM_DIR:-$HOME/.nvm}"; if [[ ! -s $NVM_DIR/nvm.sh && -n $XDG_CONFIG_HOME && -s $XDG_CONFIG_HOME/nvm/nvm.sh ]]; then export NVM_DIR="$XDG_CONFIG_HOME/nvm"; fi""");
            if (node)
            {
                commands.Add("""if ! command -v curl >/dev/null && ! command -v wget >/dev/null; then echo "缺少下载工具，正在安装 curl 和 CA 证书。"; apt_install curl ca-certificates; fi""");
                commands.Add("""if [[ ! -s $NVM_DIR/nvm.sh ]]; then command -v git >/dev/null || apt_install git ca-certificates; git -c advice.detachedHead=false clone --depth 1 --branch v0.41.0 https://github.com/nvm-sh/nvm.git "$NVM_DIR"; fi""");
                commands.Add("""if ! grep -qs '/nvm.sh' "$HOME/.bashrc"; then printf '\nexport NVM_DIR=%q\n[ ! -s "$NVM_DIR/nvm.sh" ] || . "$NVM_DIR/nvm.sh"\n' "$NVM_DIR" >> "$HOME/.bashrc"; fi""");
            }
            commands.Add("""if [[ ! -s $NVM_DIR/nvm.sh ]]; then echo "请同时勾选 Node.js，或先安装 nvm。"; exit 1; fi; source "$NVM_DIR/nvm.sh" --no-use""");
            commands.Add("""if ! /sbin/ldconfig -p 2>/dev/null | grep -F 'libatomic.so.1 ' >/dev/null; then echo "正在补装 Node.js 运行库 libatomic1。"; apt_install libatomic1; fi""");
            if (node) commands.Add("""nvm install "$node_version"; node --version; npm --version""");
            if (pnpm)
            {
                if (!node) commands.Add("nvm use default");
                commands.Add("""npm_options=(); if npm config list --long | grep -E '^[;[:space:]]*allow-scripts[[:space:]]*=' >/dev/null; then npm_options+=(--allow-scripts=pnpm); fi; npm install -g "${npm_options[@]}" "pnpm@$pnpm_version"; pnpm --version""");
            }
        }
        return string.Join("\n", commands);
    }
}
