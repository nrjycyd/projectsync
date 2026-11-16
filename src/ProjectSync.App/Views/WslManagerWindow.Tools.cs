using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ProjectSync.App.Views;

public partial class WslManagerWindow
{
    private sealed record DevelopmentTool(string Caption, string Tool)
    {
        public string Icon => $"/ProjectSync.App;component/Assets/DevelopmentTools/{Tool}.png";
    }

    private static async Task<(DevelopmentTool[] Tools, string Status)> ReadInstalledToolsAsync(string instance)
    {
        // Avoid activating Corepack downloads or package-manager version switching during a read-only check.
        const string script = """
            cd /tmp || exit 1; export COREPACK_ENABLE_NETWORK=0 COREPACK_ENABLE_AUTO_PIN=0 npm_config_manage_package_manager_versions=false; nvm_dir="$HOME/.nvm"; if [[ -n $XDG_CONFIG_HOME && -s $XDG_CONFIG_HOME/nvm/nvm.sh ]]; then nvm_dir="$XDG_CONFIG_HOME/nvm"; fi; if [[ -s $nvm_dir/nvm.sh ]]; then source "$nvm_dir/nvm.sh" --no-use && nvm use --silent default >/dev/null 2>&1; fi; for tool in git python3 node pnpm; do if version=$(command "$tool" --version 2>/dev/null); then printf '%s\t%s\n' "$tool" "$version"; fi; done
            """;
        try
        {
            var result = await InvokeAsync(new[] { "-d", instance, "--exec", "bash", "-c", script },
                encoding: Encoding.UTF8).WaitAsync(TimeSpan.FromSeconds(8));
            if (result.Code != 0) return (Array.Empty<DevelopmentTool>(), "检测失败");
            var tools = new List<DevelopmentTool>();
            foreach (string line in result.Output.Split('\n'))
            {
                var match = Regex.Match(line.TrimEnd(), @"^(git|python3|node|pnpm)\t(?:git version |Python )?v?([0-9][A-Za-z0-9.+-]*)$");
                if (!match.Success) continue;
                string tool = match.Groups[1].Value;
                string name = tool switch { "git" => "Git", "python3" => "Python", "node" => "Node.js", _ => "pnpm" };
                tools.Add(new DevelopmentTool($"{name} {match.Groups[2].Value}", tool));
            }
            return (tools.ToArray(), tools.Count == 0 ? "未检测到工具" : "");
        }
        catch (Exception error) when (error is TimeoutException or IOException or Win32Exception or InvalidOperationException)
        {
            return (Array.Empty<DevelopmentTool>(), "检测失败");
        }
    }

}
