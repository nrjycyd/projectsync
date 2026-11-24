using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SiteLift.Core;

public enum WslEnvironmentPreset
{
    Recommended,
    Node,
    Hugo,
    MkDocs,
    Jekyll,
    MdBook,
    AllMissing,
    Custom
}

public enum PresetActivation
{
    None,
    Partial,
    Active
}

public enum WslToolGroup
{
    Git,
    Node,
    Python,
    Hugo,
    Ruby,
    MdBook
}

public sealed record WslToolProbeResult(
    string? GitVersion = null,
    string? NodeVersion = null,
    string? PnpmVersion = null,
    bool HasNvm = false,
    string? PythonVersion = null,
    bool HasPythonPip = false,
    bool HasPythonVenv = false,
    bool HasPipx = false,
    string? HugoVersion = null,
    bool HugoIsExtended = false,
    string? GoVersion = null,
    string? RubyVersion = null,
    bool HasBundler = false,
    string? MdBookVersion = null,
    string? RustVersion = null,
    bool HasDownloadTool = true)
{
    public bool PythonReadyForMkDocs => PythonVersion is not null && HasPythonPip && HasPythonVenv;
    public bool HugoReady => HugoVersion is not null && HugoIsExtended;
    public bool RubyReadyForJekyll => RubyVersion is not null && HasBundler;
    public bool HasAnyTool =>
        GitVersion is not null || NodeVersion is not null || PnpmVersion is not null ||
        PythonVersion is not null || HugoVersion is not null || GoVersion is not null ||
        RubyVersion is not null || MdBookVersion is not null || RustVersion is not null;
}

public sealed record WslDevelopmentSelection(
    bool Git = false,
    string GitVersion = "latest",
    bool Node = false,
    string NodeVersion = "lts/*",
    bool Pnpm = false,
    string PnpmVersion = "latest",
    bool Corepack = false,
    bool Python = false,
    string PythonVersion = "latest",
    bool PythonPipVenv = false,
    bool PythonPipx = false,
    bool PythonAlias = false,
    bool Hugo = false,
    string HugoVersion = "latest",
    bool Go = false,
    string GoVersion = "latest",
    bool Ruby = false,
    string RubyVersion = "latest",
    bool MdBook = false,
    string MdBookVersion = "latest",
    bool Rust = false)
{
    public bool HasAnySelection =>
        Git || Node || Pnpm || Corepack || Python || PythonAlias || Hugo || Go || Ruby || MdBook || Rust;
}

public static class WslDevelopmentEnvironment
{
    /// <summary>
    /// Single read-only bash script probing all supported static-site development tools in a WSL instance.
    /// Normalized to LF so Windows CRLF source checkouts never break bash execution in WSL.
    /// </summary>
    public static string ProbeScript { get; } = NormalizeLf("""
        cd /tmp || exit 1
        export COREPACK_ENABLE_NETWORK=0 COREPACK_ENABLE_AUTO_PIN=0 npm_config_manage_package_manager_versions=false
        export PATH="$HOME/.local/bin:$HOME/.gems/bin:$HOME/.cargo/bin:/usr/local/go/bin:$PATH"
        nvm_dir="${NVM_DIR:-$HOME/.nvm}"
        if [[ ! -s $nvm_dir/nvm.sh && -n $XDG_CONFIG_HOME && -s $XDG_CONFIG_HOME/nvm/nvm.sh ]]; then nvm_dir="$XDG_CONFIG_HOME/nvm"; fi
        if [[ -s $nvm_dir/nvm.sh ]]; then
          printf 'nvm\t1\n'
          source "$nvm_dir/nvm.sh" --no-use && nvm use --silent default >/dev/null 2>&1 || true
        else
          printf 'nvm\t0\n'
        fi
        if command -v curl >/dev/null 2>&1 || command -v wget >/dev/null 2>&1; then printf 'dltool\t1\n'; else printf 'dltool\t0\n'; fi
        if command -v git >/dev/null 2>&1 && v=$(git --version 2>/dev/null | head -n1); then printf 'git\t%s\n' "$v"; fi
        if command -v node >/dev/null 2>&1 && v=$(node --version 2>/dev/null | head -n1); then printf 'node\t%s\n' "$v"; fi
        if command -v pnpm >/dev/null 2>&1 && v=$(pnpm --version 2>/dev/null | head -n1); then printf 'pnpm\t%s\n' "$v"; fi
        if command -v python3 >/dev/null 2>&1 && v=$(python3 --version 2>/dev/null | head -n1); then
          printf 'python3\t%s\n' "$v"
          if python3 -m pip --version >/dev/null 2>&1; then printf 'pypip\t1\n'; else printf 'pypip\t0\n'; fi
          if python3 -c "import venv, ensurepip" >/dev/null 2>&1; then printf 'pyvenv\t1\n'; else printf 'pyvenv\t0\n'; fi
        fi
        if command -v pipx >/dev/null 2>&1; then printf 'pipx\t1\n'; else printf 'pipx\t0\n'; fi
        if command -v hugo >/dev/null 2>&1 && v=$(hugo version 2>/dev/null | head -n1); then printf 'hugo\t%s\n' "$v"; fi
        if command -v go >/dev/null 2>&1 && v=$(go version 2>/dev/null | head -n1); then printf 'go\t%s\n' "$v"; fi
        if command -v ruby >/dev/null 2>&1 && v=$(ruby --version 2>/dev/null | head -n1); then printf 'ruby\t%s\n' "$v"; fi
        if command -v bundle >/dev/null 2>&1; then printf 'bundler\t1\n'; else printf 'bundler\t0\n'; fi
        if command -v mdbook >/dev/null 2>&1 && v=$(mdbook --version 2>/dev/null | head -n1); then printf 'mdbook\t%s\n' "$v"; fi
        if command -v rustc >/dev/null 2>&1 && v=$(rustc --version 2>/dev/null | head -n1); then printf 'rustc\t%s\n' "$v"; fi
        """);

    private static string NormalizeLf(string script) =>
        script.Replace("\r\n", "\n").Replace('\r', '\n');

    public static WslToolProbeResult ParseProbeOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return new WslToolProbeResult();

        string? git = null, node = null, pnpm = null, python = null, hugo = null, go = null, ruby = null, mdbook = null, rust = null;
        bool hasNvm = false, hasPip = false, hasVenv = false, hasPipx = false, hugoExtended = false, hasBundler = false;
        bool hasDownloadTool = true;

        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r', ' ');
            int tab = line.IndexOf('\t');
            if (tab <= 0) continue;

            string key = line[..tab];
            string val = line[(tab + 1)..].Trim();
            switch (key)
            {
                case "nvm": hasNvm = val == "1"; break;
                case "dltool": hasDownloadTool = val == "1"; break;
                case "pypip": hasPip = val == "1"; break;
                case "pyvenv": hasVenv = val == "1"; break;
                case "pipx": hasPipx = val == "1"; break;
                case "bundler": hasBundler = val == "1"; break;
                case "git": git = ExtractVersionToken(val, @"git version\s+v?([0-9][A-Za-z0-9.+-]*)"); break;
                case "node": node = ExtractVersionToken(val, @"\Av?([0-9][A-Za-z0-9.+-]*)"); break;
                case "pnpm": pnpm = ExtractVersionToken(val, @"\Av?([0-9][A-Za-z0-9.+-]*)"); break;
                case "python3": python = ExtractVersionToken(val, @"Python\s+([0-9][A-Za-z0-9.+-]*)"); break;
                case "hugo":
                    hugo = ExtractVersionToken(val, @"v([0-9]+\.[0-9]+\.[0-9]+)");
                    hugoExtended = val.Contains("extended", StringComparison.OrdinalIgnoreCase);
                    break;
                case "go": go = ExtractVersionToken(val, @"go([0-9]+\.[0-9]+(?:\.[0-9]+)?)"); break;
                case "ruby": ruby = ExtractVersionToken(val, @"ruby\s+([0-9]+\.[0-9]+\.[0-9]+)"); break;
                case "mdbook": mdbook = ExtractVersionToken(val, @"mdbook\s+v?([0-9]+\.[0-9]+\.[0-9]+)"); break;
                case "rustc": rust = ExtractVersionToken(val, @"rustc\s+([0-9]+\.[0-9]+\.[0-9]+)"); break;
            }
        }

        return new WslToolProbeResult(
            GitVersion: git,
            NodeVersion: node,
            PnpmVersion: pnpm,
            HasNvm: hasNvm,
            PythonVersion: python,
            HasPythonPip: hasPip,
            HasPythonVenv: hasVenv,
            HasPipx: hasPipx,
            HugoVersion: hugo,
            HugoIsExtended: hugoExtended,
            GoVersion: go,
            RubyVersion: ruby,
            HasBundler: hasBundler,
            MdBookVersion: mdbook,
            RustVersion: rust,
            HasDownloadTool: hasDownloadTool);
    }

    private static string? ExtractVersionToken(string value, string pattern)
    {
        var match = Regex.Match(value, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static WslToolGroup? MapFrameworkToGroup(SiteFramework framework) => framework switch
    {
        SiteFramework.VuePress or SiteFramework.VitePress or SiteFramework.Hexo or
        SiteFramework.Astro or SiteFramework.Docusaurus or SiteFramework.Quartz or
        SiteFramework.Rspress or SiteFramework.Nuxt or SiteFramework.NextJs or
        SiteFramework.Docsify => WslToolGroup.Node,
        SiteFramework.Hugo => WslToolGroup.Hugo,
        SiteFramework.MkDocs => WslToolGroup.Python,
        SiteFramework.Jekyll => WslToolGroup.Ruby,
        SiteFramework.MdBook => WslToolGroup.MdBook,
        _ => null
    };

    public static IReadOnlyList<WslToolGroup> LinkedFrameworkGroups(IEnumerable<SiteFramework> linkedFrameworks)
    {
        var set = new HashSet<WslToolGroup>();
        foreach (var fw in linkedFrameworks)
        {
            if (MapFrameworkToGroup(fw) is { } group)
                set.Add(group);
        }
        if (set.Count == 0)
            return Array.Empty<WslToolGroup>();

        var ordered = new List<WslToolGroup> { WslToolGroup.Git };
        foreach (var candidate in new[] { WslToolGroup.Node, WslToolGroup.Python, WslToolGroup.Hugo, WslToolGroup.Ruby, WslToolGroup.MdBook })
        {
            if (set.Contains(candidate))
                ordered.Add(candidate);
        }
        return ordered;
    }

    public static PresetActivation GetPresetActivation(
        WslEnvironmentPreset preset,
        WslToolProbeResult probe,
        IReadOnlyCollection<SiteFramework> linkedFrameworks)
    {
        switch (preset)
        {
            case WslEnvironmentPreset.Node:
                if (probe.GitVersion is not null && probe.NodeVersion is not null && probe.PnpmVersion is not null)
                    return PresetActivation.Active;
                return probe.NodeVersion is not null ? PresetActivation.Partial : PresetActivation.None;

            case WslEnvironmentPreset.Hugo:
                if (probe.GitVersion is not null && probe.HugoReady)
                    return PresetActivation.Active;
                return probe.HugoVersion is not null ? PresetActivation.Partial : PresetActivation.None;

            case WslEnvironmentPreset.MkDocs:
                if (probe.GitVersion is not null && probe.PythonReadyForMkDocs)
                    return PresetActivation.Active;
                return probe.PythonVersion is not null ? PresetActivation.Partial : PresetActivation.None;

            case WslEnvironmentPreset.Jekyll:
                if (probe.GitVersion is not null && probe.RubyReadyForJekyll)
                    return PresetActivation.Active;
                return probe.RubyVersion is not null ? PresetActivation.Partial : PresetActivation.None;

            case WslEnvironmentPreset.MdBook:
                if (probe.GitVersion is not null && probe.MdBookVersion is not null)
                    return PresetActivation.Active;
                return probe.MdBookVersion is not null ? PresetActivation.Partial : PresetActivation.None;

            case WslEnvironmentPreset.Recommended:
            {
                var groups = LinkedFrameworkGroups(linkedFrameworks);
                if (groups.Count == 0)
                    return PresetActivation.None;
                bool allActive = groups.All(g => IsGroupReady(g, probe));
                if (allActive)
                    return PresetActivation.Active;
                bool anyStarted = groups.Any(g => g != WslToolGroup.Git && IsGroupPartiallyPresent(g, probe));
                return anyStarted ? PresetActivation.Partial : PresetActivation.None;
            }

            default:
                return PresetActivation.None;
        }
    }

    public static bool IsGroupReady(WslToolGroup group, WslToolProbeResult probe) => group switch
    {
        WslToolGroup.Git => probe.GitVersion is not null,
        WslToolGroup.Node => probe.NodeVersion is not null && probe.PnpmVersion is not null,
        WslToolGroup.Python => probe.PythonReadyForMkDocs,
        WslToolGroup.Hugo => probe.HugoReady,
        WslToolGroup.Ruby => probe.RubyReadyForJekyll,
        WslToolGroup.MdBook => probe.MdBookVersion is not null,
        _ => false
    };

    private static bool IsGroupPartiallyPresent(WslToolGroup group, WslToolProbeResult probe) => group switch
    {
        WslToolGroup.Git => probe.GitVersion is not null,
        WslToolGroup.Node => probe.NodeVersion is not null || probe.PnpmVersion is not null,
        WslToolGroup.Python => probe.PythonVersion is not null,
        WslToolGroup.Hugo => probe.HugoVersion is not null,
        WslToolGroup.Ruby => probe.RubyVersion is not null,
        WslToolGroup.MdBook => probe.MdBookVersion is not null,
        _ => false
    };

    public static IReadOnlyList<WslToolGroup> GetVisibleGroups(
        WslEnvironmentPreset preset,
        WslToolProbeResult probe,
        IReadOnlyCollection<SiteFramework> linkedFrameworks) => preset switch
    {
        WslEnvironmentPreset.Node => new[] { WslToolGroup.Git, WslToolGroup.Node },
        WslEnvironmentPreset.Hugo => new[] { WslToolGroup.Git, WslToolGroup.Hugo },
        WslEnvironmentPreset.MkDocs => new[] { WslToolGroup.Git, WslToolGroup.Python },
        WslEnvironmentPreset.Jekyll => new[] { WslToolGroup.Git, WslToolGroup.Ruby },
        WslEnvironmentPreset.MdBook => new[] { WslToolGroup.Git, WslToolGroup.MdBook },
        WslEnvironmentPreset.Recommended => LinkedFrameworkGroups(linkedFrameworks) is { Count: > 0 } linked
            ? linked
            : new[] { WslToolGroup.Git, WslToolGroup.Node },
        WslEnvironmentPreset.AllMissing => GetMissingGroupsOrAll(probe),
        _ => new[] { WslToolGroup.Git, WslToolGroup.Node, WslToolGroup.Python, WslToolGroup.Hugo, WslToolGroup.Ruby, WslToolGroup.MdBook }
    };

    private static IReadOnlyList<WslToolGroup> GetMissingGroupsOrAll(WslToolProbeResult probe)
    {
        var all = new[] { WslToolGroup.Git, WslToolGroup.Node, WslToolGroup.Python, WslToolGroup.Hugo, WslToolGroup.Ruby, WslToolGroup.MdBook };
        var missing = all.Where(g => !IsGroupReady(g, probe)).ToArray();
        return missing.Length > 0 ? missing : all;
    }

    public static WslDevelopmentSelection BuildPresetSelection(
        WslEnvironmentPreset preset,
        WslToolProbeResult probe,
        IReadOnlyCollection<SiteFramework> linkedFrameworks)
    {
        if (preset == WslEnvironmentPreset.Custom)
            return new WslDevelopmentSelection();

        var groups = GetVisibleGroups(preset, probe, linkedFrameworks);
        bool git = groups.Contains(WslToolGroup.Git) && probe.GitVersion is null;
        bool node = groups.Contains(WslToolGroup.Node) && probe.NodeVersion is null;
        bool pnpm = groups.Contains(WslToolGroup.Node) && probe.PnpmVersion is null;
        if (pnpm && !probe.HasNvm && !node)
            node = true;

        bool python = groups.Contains(WslToolGroup.Python) && !probe.PythonReadyForMkDocs;
        bool pyPipVenv = python;
        bool pyPipx = python && !probe.HasPipx;
        bool hugo = groups.Contains(WslToolGroup.Hugo) && !probe.HugoReady;
        bool ruby = groups.Contains(WslToolGroup.Ruby) && !probe.RubyReadyForJekyll;
        bool mdbook = groups.Contains(WslToolGroup.MdBook) && probe.MdBookVersion is null;

        return new WslDevelopmentSelection(
            Git: git,
            Node: node,
            Pnpm: pnpm,
            Python: python,
            PythonPipVenv: pyPipVenv,
            PythonPipx: pyPipx,
            Hugo: hugo,
            Ruby: ruby,
            MdBook: mdbook);
    }

    public static bool IsValidAptVersion(string version) =>
        version == "latest" || Regex.IsMatch(version, @"\A[0-9][A-Za-z0-9.+:~\-]*\z");

    public static bool IsValidNodeVersion(string version) =>
        Regex.IsMatch(version, @"\A(?:latest|lts/\*|v?[0-9]+(?:\.[0-9]+){0,2})\z");

    public static bool IsValidPnpmVersion(string version) =>
        version == "latest" || Regex.IsMatch(version, @"\A[0-9]+(?:\.[0-9]+){0,2}(?:-[A-Za-z0-9.-]+)?\z");

    public static bool IsValidSemVerTag(string version) =>
        version == "latest" || Regex.IsMatch(version, @"\Av?[0-9]+\.[0-9]+\.[0-9]+\z");

    public static bool IsValidGoVersion(string version) =>
        version == "latest" || Regex.IsMatch(version, @"\A(?:go)?[0-9]+\.[0-9]+(?:\.[0-9]+)?\z");

    public static bool IsValidSelection(WslDevelopmentSelection selection) =>
        selection.HasAnySelection &&
        (!selection.Git || IsValidAptVersion(selection.GitVersion)) &&
        (!selection.Node || IsValidNodeVersion(selection.NodeVersion)) &&
        (!selection.Pnpm || IsValidPnpmVersion(selection.PnpmVersion)) &&
        (!selection.Python || IsValidAptVersion(selection.PythonVersion)) &&
        (!selection.Hugo || IsValidSemVerTag(selection.HugoVersion)) &&
        (!selection.Go || IsValidGoVersion(selection.GoVersion)) &&
        (!selection.Ruby || IsValidAptVersion(selection.RubyVersion)) &&
        (!selection.MdBook || IsValidSemVerTag(selection.MdBookVersion));

    public static bool NeedsDownloadTool(WslDevelopmentSelection selection) =>
        selection.Node || selection.Hugo || selection.Go || selection.MdBook || selection.Rust;

    public static bool RequiresSudo(WslDevelopmentSelection selection, WslToolProbeResult probe) =>
        selection.Git || selection.Python || selection.PythonAlias ||
        selection.Hugo || selection.Go || selection.Ruby || selection.MdBook ||
        (!probe.HasDownloadTool && NeedsDownloadTool(selection));

    public static IReadOnlyList<string> DescribeSelection(WslDevelopmentSelection selection)
    {
        var list = new List<string>();
        if (selection.Git) list.Add($"Git ({selection.GitVersion})");
        if (selection.Node) list.Add($"Node.js ({selection.NodeVersion})");
        if (selection.Pnpm) list.Add($"pnpm ({selection.PnpmVersion})");
        if (selection.Corepack) list.Add("Corepack/Yarn");
        if (selection.Python || selection.PythonAlias)
        {
            var extras = new List<string>();
            if (selection.Python && selection.PythonPipVenv) extras.Add("pip/venv");
            if (selection.Python && selection.PythonPipx) extras.Add("pipx");
            if (selection.PythonAlias) extras.Add("python别名");
            string suffix = extras.Count > 0 ? $" + {string.Join("/", extras)}" : "";
            list.Add(selection.Python ? $"Python 3 ({selection.PythonVersion}){suffix}" : "python-is-python3");
        }
        if (selection.Hugo) list.Add($"Hugo Extended ({selection.HugoVersion})");
        if (selection.Go) list.Add($"Go ({selection.GoVersion})");
        if (selection.Ruby) list.Add($"Ruby & Bundler ({selection.RubyVersion})");
        if (selection.MdBook) list.Add($"mdBook ({selection.MdBookVersion})");
        if (selection.Rust) list.Add("Rust (rustup)");
        return list;
    }

    public static (string Script, string[] Arguments) BuildSetupCommand(WslDevelopmentSelection selection)
    {
        if (!IsValidSelection(selection))
            throw new ArgumentException("无效的开发环境组件或版本选择。", nameof(selection));

        var aptPackages = new List<string>();
        if (selection.Git) aptPackages.Add(AptPackage("git", selection.GitVersion));
        if (selection.Python)
        {
            aptPackages.Add(AptPackage("python3", selection.PythonVersion));
            if (selection.PythonPipVenv)
            {
                aptPackages.Add("python3-pip");
                aptPackages.Add("python3-venv");
            }
            if (selection.PythonPipx)
                aptPackages.Add("pipx");
        }
        if (selection.PythonAlias) aptPackages.Add("python-is-python3");
        if (selection.Ruby)
        {
            aptPackages.Add(AptPackage("ruby-full", selection.RubyVersion));
            aptPackages.Add("build-essential");
            aptPackages.Add("zlib1g-dev");
        }

        var commands = new List<string>
        {
            """trap 'status=$?; printf "\n安装退出代码：%s\n按 Enter 关闭终端…" "$status"; read -r _ || true; exit "$status"' EXIT""",
            "set -e -o pipefail",
            """node_version=$1; pnpm_version=$2; hugo_version=$3; go_version=$4; mdbook_version=$5; shift 5""",
            """run_root() { if [[ $EUID == 0 ]]; then "$@"; else sudo "$@"; fi; }""",
            """apt_updated=0; apt_install() { command -v apt-get >/dev/null || { echo "当前发行版没有 APT。"; return 1; }; if [[ $apt_updated == 0 ]]; then run_root apt-get update; apt_updated=1; fi; run_root apt-get install -y -- "$@"; }""",
            """ensure_line() { local file=$1 line=$2; touch "$file"; grep -Fqx "$line" "$file" || printf '\n%s\n' "$line" >> "$file"; }""",
            """fetch_url() { if command -v curl >/dev/null 2>&1; then curl -fsSL "$1"; else wget -qO- "$1"; fi; }""",
            """download_file() { if command -v curl >/dev/null 2>&1; then curl -fsSL "$1" -o "$2"; else wget -qO "$2" "$1"; fi; }"""
        };

        if (aptPackages.Count > 0)
        {
            commands.Add("""apt_install "$@"; for package in "$@"; do case "${package%%=*}" in git) git --version;; python3) python3 --version;; python-is-python3) python --version;; pipx) pipx --version;; ruby-full) ruby --version;; esac; done""");
            if (selection.Python && selection.PythonPipx)
                commands.Add("""if command -v pipx >/dev/null 2>&1; then pipx ensurepath >/dev/null 2>&1 || true; fi""");
        }

        if (NeedsDownloadTool(selection))
        {
            commands.Add("""if ! command -v curl >/dev/null 2>&1 && ! command -v wget >/dev/null 2>&1; then echo "缺少下载工具，正在安装 curl 和 CA 证书。"; apt_install curl ca-certificates; fi""");
            commands.Add("""if ! command -v tar >/dev/null 2>&1; then apt_install tar gzip; fi""");
        }

        if (selection.Node || selection.Pnpm || selection.Corepack)
        {
            commands.Add("""export NVM_DIR="${NVM_DIR:-$HOME/.nvm}"; if [[ ! -s $NVM_DIR/nvm.sh && -n $XDG_CONFIG_HOME && -s $XDG_CONFIG_HOME/nvm/nvm.sh ]]; then export NVM_DIR="$XDG_CONFIG_HOME/nvm"; fi""");
            if (selection.Node)
            {
                commands.Add("""if [[ ! -s $NVM_DIR/nvm.sh ]]; then command -v git >/dev/null 2>&1 || apt_install git ca-certificates; git -c advice.detachedHead=false clone --depth 1 --branch v0.41.0 https://github.com/nvm-sh/nvm.git "$NVM_DIR"; fi""");
                commands.Add("""if ! grep -qs '/nvm.sh' "$HOME/.bashrc"; then printf '\nexport NVM_DIR=%q\n[ ! -s "$NVM_DIR/nvm.sh" ] || . "$NVM_DIR/nvm.sh"\n' "$NVM_DIR" >> "$HOME/.bashrc"; fi""");
            }
            commands.Add("""if [[ ! -s $NVM_DIR/nvm.sh ]]; then echo "请同时开启 Node.js（nvm），或先安装 nvm。"; exit 1; fi; source "$NVM_DIR/nvm.sh" --no-use""");
            commands.Add("""if ! /sbin/ldconfig -p 2>/dev/null | grep -F 'libatomic.so.1 ' >/dev/null; then echo "正在补装 Node.js 运行库 libatomic1。"; apt_install libatomic1; fi""");
            if (selection.Node)
                commands.Add("""nvm install "$node_version"; node --version; npm --version""");
            if (selection.Pnpm || selection.Corepack)
            {
                if (!selection.Node)
                    commands.Add("nvm use default");
                if (selection.Pnpm)
                    commands.Add("""npm_options=(); if npm config list --long | grep -E '^[;[:space:]]*allow-scripts[[:space:]]*=' >/dev/null; then npm_options+=(--allow-scripts=pnpm); fi; npm install -g "${npm_options[@]}" "pnpm@$pnpm_version"; pnpm --version""");
                if (selection.Corepack)
                    commands.Add("""corepack enable; corepack --version""");
            }
        }

        if (selection.Hugo)
        {
            commands.Add("""
                echo "正在安装 Hugo Extended..."
                arch=$(uname -m); case "$arch" in x86_64) hugo_arch=amd64;; aarch64|arm64) hugo_arch=arm64;; *) echo "不支持的 CPU 架构：$arch"; exit 1;; esac
                if [[ $hugo_version == "latest" ]]; then
                  hugo_tag=$(fetch_url "https://api.github.com/repos/gohugoio/hugo/releases/latest" | grep -m1 '"tag_name"' | sed -E 's/.*"v?([^"]+)".*/v\1/')
                else
                  hugo_tag="v${hugo_version#v}"
                fi
                [[ -n $hugo_tag ]] || { echo "无法解析 Hugo 最新版本号。"; exit 1; }
                hugo_ver="${hugo_tag#v}"
                hugo_tmp=$(mktemp -d)
                download_file "https://github.com/gohugoio/hugo/releases/download/${hugo_tag}/hugo_extended_${hugo_ver}_linux-${hugo_arch}.tar.gz" "$hugo_tmp/hugo.tar.gz"
                tar -xzf "$hugo_tmp/hugo.tar.gz" -C "$hugo_tmp" hugo
                run_root install -m 0755 "$hugo_tmp/hugo" /usr/local/bin/hugo
                rm -rf "$hugo_tmp"
                hugo version
                """);
        }

        if (selection.Go)
        {
            commands.Add("""
                echo "正在安装 Go..."
                arch=$(uname -m); case "$arch" in x86_64) go_arch=amd64;; aarch64|arm64) go_arch=arm64;; *) echo "不支持的 CPU 架构：$arch"; exit 1;; esac
                if [[ $go_version == "latest" ]]; then
                  go_tag=$(fetch_url "https://go.dev/VERSION?m=text" | head -n1 | tr -d '\r')
                else
                  go_tag="go${go_version#go}"
                fi
                [[ $go_tag =~ ^go[0-9]+\.[0-9]+ ]] || { echo "无法解析 Go 版本号。"; exit 1; }
                go_tmp=$(mktemp -d)
                download_file "https://go.dev/dl/${go_tag}.linux-${go_arch}.tar.gz" "$go_tmp/go.tar.gz"
                run_root rm -rf /usr/local/go
                run_root tar -C /usr/local -xzf "$go_tmp/go.tar.gz"
                run_root ln -sf /usr/local/go/bin/go /usr/local/bin/go
                run_root ln -sf /usr/local/go/bin/gofmt /usr/local/bin/gofmt
                rm -rf "$go_tmp"
                ensure_line "$HOME/.bashrc" 'export PATH="/usr/local/go/bin:$PATH"'
                ensure_line "$HOME/.profile" 'export PATH="/usr/local/go/bin:$PATH"'
                go version
                """);
        }

        if (selection.Ruby)
        {
            commands.Add("""
                echo "正在配置用户级 Ruby Gems 与 Bundler（Jekyll）..."
                export GEM_HOME="$HOME/.gems"
                export PATH="$HOME/.gems/bin:$PATH"
                mkdir -p "$GEM_HOME"
                ensure_line "$HOME/.bashrc" 'export GEM_HOME="$HOME/.gems"'
                ensure_line "$HOME/.bashrc" 'export PATH="$HOME/.gems/bin:$PATH"'
                ensure_line "$HOME/.profile" 'export GEM_HOME="$HOME/.gems"'
                ensure_line "$HOME/.profile" 'export PATH="$HOME/.gems/bin:$PATH"'
                gem install --no-document bundler
                ruby --version
                bundle --version
                """);
        }

        if (selection.MdBook)
        {
            commands.Add("""
                echo "正在安装 mdBook（预编译二进制）..."
                arch=$(uname -m); case "$arch" in x86_64) md_target=x86_64-unknown-linux-gnu;; aarch64|arm64) md_target=aarch64-unknown-linux-gnu;; *) echo "不支持的 CPU 架构：$arch"; exit 1;; esac
                if [[ $mdbook_version == "latest" ]]; then
                  md_tag=$(fetch_url "https://api.github.com/repos/rust-lang/mdBook/releases/latest" | grep -m1 '"tag_name"' | sed -E 's/.*"v?([^"]+)".*/v\1/')
                else
                  md_tag="v${mdbook_version#v}"
                fi
                [[ -n $md_tag ]] || { echo "无法解析 mdBook 最新版本号。"; exit 1; }
                md_tmp=$(mktemp -d)
                download_file "https://github.com/rust-lang/mdBook/releases/download/${md_tag}/mdbook-${md_tag}-${md_target}.tar.gz" "$md_tmp/mdbook.tar.gz"
                tar -xzf "$md_tmp/mdbook.tar.gz" -C "$md_tmp" mdbook
                run_root install -m 0755 "$md_tmp/mdbook" /usr/local/bin/mdbook
                rm -rf "$md_tmp"
                mdbook --version
                """);
        }

        if (selection.Rust)
        {
            commands.Add("""
                echo "正在安装 Rust（rustup）..."
                if ! command -v rustup >/dev/null 2>&1; then
                  if command -v curl >/dev/null 2>&1; then
                    curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y
                  else
                    wget -qO- https://sh.rustup.rs | sh -s -- -y
                  fi
                else
                  rustup update stable
                fi
                if [[ -s "$HOME/.cargo/env" ]]; then source "$HOME/.cargo/env"; fi
                rustc --version
                cargo --version
                """);
        }

        var args = new List<string>
        {
            selection.NodeVersion == "latest" ? "node" : selection.NodeVersion,
            selection.PnpmVersion,
            selection.HugoVersion,
            selection.GoVersion,
            selection.MdBookVersion
        };
        args.AddRange(aptPackages);

        return (NormalizeLf(string.Join("\n", commands)), args.ToArray());
    }

    private static string AptPackage(string name, string version) =>
        version == "latest" ? name : $"{name}={version}";
}
