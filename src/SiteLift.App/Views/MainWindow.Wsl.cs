using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using SiteLift.Core;
using Wpf.Ui.Controls;

namespace SiteLift.App.Views;

public partial class MainWindow
{
    private bool detectingWsl;

    private void UpdateWslEnvironmentUi()
    {
        string path = wslRepo.Text.Trim();
        string selectedDistro = distro.Text.Trim();
        string selectedUser = wslUser.Text.Trim();

        if (path.Length == 0)
        {
            WslEnvBadge.Content = "未配置";
            WslEnvBadge.Appearance = ControlAppearance.Secondary;
        }
        else if (selectedDistro.Length == 0 || selectedUser.Length == 0)
        {
            WslEnvBadge.Content = "待检测";
            WslEnvBadge.Appearance = ControlAppearance.Caution;
        }
        else
        {
            WslEnvBadge.Content = $"{selectedDistro} · {selectedUser}";
            WslEnvBadge.Appearance = ControlAppearance.Success;
        }

        string extraPath = nodeBin.Text.Trim();
        NodeBinSummary.Text = extraPath.Length > 0
            ? $"当前已配置：{extraPath}"
            : "留空将使用 WSL 默认 PATH；选择上方 Node.js 版本时会自动填入";

        bool hasReinstall = !string.IsNullOrWhiteSpace(reinstallCommand.Text);
        bool hasUpgrade = !string.IsNullOrWhiteSpace(themeUpgradeCommand.Text);
        MaintenanceCommandSummary.Text = (hasReinstall, hasUpgrade) switch
        {
            (true, true) => "已配置：重装依赖、升级主题",
            (true, false) => "已配置：重装依赖",
            (false, true) => "已配置：升级主题",
            _ => "尚未配置；展开可设置首页快捷按钮执行的「重装依赖」与「升级主题」命令"
        };
    }

    private async void DetectWsl(object sender, RoutedEventArgs e)
    {
        if (detectingWsl || running || !TryGetWslPath(out string path, out string namedDistro))
            return;

        detectingWsl = true;
        SettingsTabs.IsEnabled = Actions.IsEnabled = false;
        try
        {
            EnvironmentFeedback.ShowFeedback("正在查找 WSL 项目目录…");
            string[] distributions = namedDistro.Length > 0 ? new[] { namedDistro } :
                Lines(await QueryWslAsync(Encoding.Unicode, "-l", "-q"))
                    .Where(name => !WslOutputParser.IsSystemUtilityDistribution(name))
                    .ToArray();
            var matches = new List<string>();
            foreach (string name in distributions)
            {
                await QueryWslAsync(Encoding.UTF8, new[] { "-d", name, "-u", "root", "--", "test", "-d", path },
                    allowFailure: true, onSuccess: () => matches.Add(name));
            }

            if (matches.Count == 0)
                throw new InvalidOperationException("没有找到包含此项目目录的 WSL 发行版。");

            if (namedDistro.Length > 0)
                wslRepo.Text = path;
            string previous = distro.Text;
            string? cacheError = SaveChoices(DistributionChoiceKey(), matches);
            WslDistribution.ItemsSource = matches;
            WslDistribution.SelectedItem = matches.Contains(previous) ? previous : matches.Count == 1 ? matches[0] : null;
            if (WslDistribution.SelectedItem is string selected)
            {
                await LoadWslUsersAsync(selected, path);
                if (WslUserChoice.SelectedItem is string user)
                    await DetectNodeAsync(selected, user, path);
            }
            else
            {
                distro.Clear();
                wslUser.Clear();
                WslUserChoice.ItemsSource = null;
                EnvironmentFeedback.ShowFeedback("多个发行版包含此仓库，请选择一个发行版。");
            }
            if (cacheError is not null)
                EnvironmentFeedback.ShowFeedback(EnvironmentFeedback.Message + Environment.NewLine + cacheError, InfoBarSeverity.Warning);
        }
        catch (Exception error) { EnvironmentFeedback.ShowFeedback(error.Message, InfoBarSeverity.Error); }
        finally
        {
            detectingWsl = false;
            SettingsTabs.IsEnabled = Actions.IsEnabled = true;
        }
    }

    private async void ChooseWslDistribution(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (loading || detectingWsl || WslDistribution.SelectedItem is not string name || !TryGetWslPath(out string path, out _))
            return;
        detectingWsl = true;
        SettingsTabs.IsEnabled = Actions.IsEnabled = false;
        EnvironmentFeedback.ShowFeedback("正在读取 WSL 用户…");
        try { await LoadWslUsersAsync(name, path); }
        catch (Exception error) { EnvironmentFeedback.ShowFeedback(error.Message, InfoBarSeverity.Error); }
        finally
        {
            detectingWsl = false;
            SettingsTabs.IsEnabled = Actions.IsEnabled = true;
        }
    }

    private async Task LoadWslUsersAsync(string name, string path)
    {
        string previousUser = distro.Text == name ? wslUser.Text : "";
        distro.Text = name;
        wslUser.Clear();
        WslUserChoice.ItemsSource = null;
        string defaultUser = (await QueryWslAsync(Encoding.UTF8, "-d", name, "--", "id", "-un")).Trim();
        string owner = (await QueryWslAsync(Encoding.UTF8, "-d", name, "-u", "root", "--", "stat", "-c", "%U", "--", path)).Trim();
        var accounts = Lines(await QueryWslAsync(Encoding.UTF8, "-d", name, "-u", "root", "--", "getent", "passwd"))
            .Select(line => line.Split(':'))
            .Where(parts => parts.Length == 7 && int.TryParse(parts[2], out int uid) && uid >= 1000 && uid < 65534
                && !parts[6].EndsWith("/nologin", StringComparison.Ordinal) && !parts[6].EndsWith("/false", StringComparison.Ordinal))
            .Select(parts => parts[0]);
        var users = new[] { previousUser, defaultUser, owner }.Concat(accounts)
            .Where(user => user.Length > 0 && user != "UNKNOWN").Distinct().ToArray();
        WslUserChoice.ItemsSource = users;
        WslUserChoice.SelectedItem = users.Contains(previousUser) ? previousUser :
            defaultUser == "root" && owner != "root" && users.Contains(owner) ? owner : defaultUser;
        string? cacheError = SaveChoices(UserChoiceKey(name), users);
        EnvironmentFeedback.ShowFeedback(cacheError ?? "已读取 WSL 用户。点击「检测环境」刷新 Node.js 版本。",
            cacheError is null ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);
    }

    private void ChooseWslUser(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (loading || WslUserChoice.SelectedItem is not string name) return;
        wslUser.Text = name;
        RestoreSavedNodeOption();
        if (!detectingWsl) EnvironmentFeedback.ShowFeedback("WSL 用户已修改，请重新检测 Node.js 版本。");
    }

    private sealed record NodeOption(string Label, string? Directory);

    private NodeOption SavedNodeOption()
    {
        string directory = nodeBin.Text.Trim();
        if (directory.Length == 0) return new("默认 PATH", "");
        string[] parts = directory.TrimEnd('/').Split('/');
        string label = parts.Length >= 2 && parts[^1] == "bin" && parts[^2].StartsWith('v')
            ? parts[^2] : "自定义 PATH";
        return new(label, directory);
    }

    private sealed record NodeChoices(NodeOption[] Options, string Hint);

    private static string NodeChoiceKey(string distribution, string user, string path) =>
        ChoiceKey("node", distribution, user, path);

    private void RestoreSavedNodeOption()
    {
        var saved = SavedNodeOption();
        var cached = ReadChoices<NodeChoices>(NodeChoiceKey(distro.Text.Trim(), wslUser.Text.Trim(), wslRepo.Text.Trim()));
        var options = cached?.Options?.ToList() ?? new List<NodeOption>();
        var selected = options.FirstOrDefault(option => option.Directory == saved.Directory);
        if (selected is null)
        {
            selected = saved;
            options.Add(saved);
        }

        bool wasLoading = loading;
        loading = true;
        NodeVersion.ItemsSource = options;
        NodeVersion.SelectedItem = selected;
        NodeHint.Text = cached?.Hint ?? "";
        loading = wasLoading;
    }

    private async Task DetectNodeAsync(string distribution, string user, string path)
    {
        if (user.Length == 0) return;
        EnvironmentFeedback.ShowFeedback("正在检测 Node.js 版本…");

        const string probe = """
            cd -- "$1" || exit
            if [[ -r .nvmrc ]]; then
                IFS= read -r requested < .nvmrc || true
                requested=${requested%%#*}
                printf 'requested\t%s\n' "$requested"
            fi
            if command -v node >/dev/null; then
                printf 'default\t%s\n' "$(node --version)"
            fi
            nvm_dir="${NVM_DIR:-$HOME/.nvm}"
            if [[ ! -d $nvm_dir/versions/node && -n ${XDG_CONFIG_HOME:-} && -d $XDG_CONFIG_HOME/nvm/versions/node ]]; then
                nvm_dir="$XDG_CONFIG_HOME/nvm"
            fi
            for executable in "$nvm_dir"/versions/node/*/bin/node; do
                [[ -x "$executable" ]] || continue
                bin=${executable%/node}
                version=$("$executable" --version 2>/dev/null) || continue
                printf '%s\t%s\n' "$version" "$bin"
            done
            """;
        string output = await QueryWslAsync(Encoding.UTF8, "-d", distribution, "-u", user,
            "--exec", "bash", "-c", probe.ReplaceLineEndings("\n"), "sitelift", path);
        var options = new List<NodeOption>();
        string requested = "";
        foreach (var row in Lines(output))
        {
            var values = row.Split('\t', 2);
            if (values.Length != 2) continue;
            if (values[0] == "requested") requested = values[1].Trim();
            else if (values[0] == "default") options.Add(new($"默认 PATH · {values[1]}", ""));
            else options.Add(new(values[0], values[1]));
        }

        int detectedCount = options.Count;
        string hint = requested.Length > 0 ? $".nvmrc：{requested}" : "";
        string? cacheError = SaveChoices(NodeChoiceKey(distribution, user, path), new NodeChoices(options.ToArray(), hint));
        var saved = SavedNodeOption();
        if (options.All(option => option.Directory != saved.Directory) && saved.Directory != "")
            options.Add(saved);
        NodeVersion.ItemsSource = options;
        var existing = options.FirstOrDefault(option => option.Directory == nodeBin.Text.Trim());
        if (nodeBin.Text.Trim().Length > 0)
            NodeVersion.SelectedItem = existing;
        else if (requested.Length > 0)
        {
            string prefix = requested.StartsWith('v') ? requested : "v" + requested;
            var preferred = options.Where(option => option.Directory?.Length > 0 &&
                (option.Label == prefix || option.Label.StartsWith(prefix + ".", StringComparison.Ordinal))).ToArray();
            if (preferred.Length == 1) NodeVersion.SelectedItem = preferred[0];
        }
        else if (existing is not null || options.Count == 1)
            NodeVersion.SelectedItem = existing ?? options[0];

        NodeHint.Text = hint;
        string message = detectedCount == 0
            ? "未检测到 Node.js。可在下方手动填写额外 PATH 目录。"
            : $"检测到 {detectedCount} 个 Node.js 选项。请核对所选版本，版本设置随项目保存。";
        if (cacheError is not null) message += Environment.NewLine + cacheError;
        EnvironmentFeedback.ShowFeedback(message, detectedCount == 0 || cacheError is not null ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
    }

    private void ChooseNodeVersion(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!loading && NodeVersion.SelectedItem is NodeOption option)
            nodeBin.Text = option.Directory ?? "";
    }

    private bool TryGetWslPath(out string path, out string distribution)
    {
        path = UnquotePath(wslRepo.Text);
        if (TryParseWslNetworkPath(path, out var linuxPath, out distribution))
        {
            path = linuxPath;
            return true;
        }
        if (path.StartsWith("/", StringComparison.Ordinal) && !path.StartsWith("//", StringComparison.Ordinal))
            return true;
        EnvironmentFeedback.ShowFeedback("请输入 Linux 绝对路径或 WSL 网络路径。", InfoBarSeverity.Warning);
        return false;
    }

    private static bool TryParseWslNetworkPath(string value, out string path, out string distribution) =>
        SyncPath.TryParseWslNetworkPath(value, out path, out distribution);

    private static string[] Lines(string text) =>
        text.TrimStart('\uFEFF').Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static Task<string> QueryWslAsync(Encoding encoding, params string[] args) =>
        QueryWslAsync(encoding, args, false, null);

    private static async Task<string> QueryWslAsync(Encoding encoding, string[] args, bool allowFailure, Action? onSuccess)
    {
        var result = await ProcessRunner.RunWslAsync(args, encoding: encoding, timeout: TimeSpan.FromSeconds(15));
        if (result.TimedOut)
            throw new InvalidOperationException("WSL 查询超时，请确认发行版能够启动。");
        if (result.ExitCode != 0 && !allowFailure)
            throw new InvalidOperationException($"WSL 查询失败：{result.Stderr.Trim()}");
        if (result.ExitCode == 0) onSuccess?.Invoke();
        return result.Stdout;
    }
}
