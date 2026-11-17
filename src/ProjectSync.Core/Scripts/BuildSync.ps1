param(
    [string]$Config = (Join-Path $PSScriptRoot 'build-sync.json'),
    [switch]$Preview,
    [switch]$AllowCleanup,
    [string]$BuildLease,
    [string]$ResultFile,
    [switch]$Publish,
    [switch]$TestConnection,
    [switch]$ListRemotes,
    [switch]$CheckWindowsGit,
    [ValidateSet('empty', 'existing', 'reset')]
    [string]$InitializeRepository,
    [string]$ExpectedPlan,
    [string]$CommitSummaryFile
)
$ErrorActionPreference = 'Stop'
if ($env:PROJECTSYNC_BUNDLED_SCRIPT_LAUNCH -eq '1') {
    # The app authorizes this bundled script only. Commands it starts inherit the caller's policy.
    $env:PSExecutionPolicyPreference = $env:PROJECTSYNC_PARENT_EXECUTION_POLICY
    Remove-Item Env:PROJECTSYNC_BUNDLED_SCRIPT_LAUNCH, Env:PROJECTSYNC_PARENT_EXECUTION_POLICY -ErrorAction SilentlyContinue
}
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
function Write-SyncResult([string]$Kind, [string]$Value) {
    if ($ResultFile) {
        $result = @{ kind = $Kind; value = $Value } | ConvertTo-Json -Compress
        [IO.File]::WriteAllText($ResultFile, $result)
    } else {
        Write-Host "$Kind=$Value"
    }
}
function Stop-Sync([string]$Code, [string]$Detail) {
    Write-SyncResult 'PROJECTSYNC_ERROR' $Code
    Write-Host $Detail
    exit 1
}
$c = Get-Content -LiteralPath $Config -Raw -Encoding UTF8 | ConvertFrom-Json
if (!$CheckWindowsGit -and !$ListRemotes -and !$TestConnection -and !$InitializeRepository) {
    $missing = @('windowsRepo', 'wslRepo', 'distro', 'wslUser', 'buildCommand', 'branch', 'include' |
        Where-Object { !$c.$_ })
    if ($missing.Count -gt 0) { Stop-Sync 'CONFIG' "Missing fields: $($missing -join ', ')" }
}
if (!(Get-Command git.exe -CommandType Application -ErrorAction SilentlyContinue)) {
    Stop-Sync 'WINDOWS_GIT_MISSING' 'Git for Windows (git.exe) was not found on the Windows PATH.'
}
if ($CheckWindowsGit) {
    git.exe --version
    exit $LASTEXITCODE
}
$remoteUrl = ([string]$c.remoteUrl).Trim()
$gitOptions = @()
if ($InitializeRepository -or ($TestConnection -and !$ListRemotes -and $remoteUrl)) {
    # An explicit URL can be queried before the local repository is initialized.
    $gitOptions = @('-C', [IO.Path]::GetTempPath())
} else {
    if (![IO.Directory]::Exists([string]$c.windowsRepo)) {
        if ($TestConnection) {
            Stop-Sync 'CONFIG' 'Enter a remote repository URL to test a connection without a local Git repository.'
        }
        Stop-Sync 'WINDOWS_REPO' 'Windows 项目目录不存在或不可访问，请在「Git 本地仓库」选择目录。'
    }
    $repo = (Resolve-Path -LiteralPath $c.windowsRepo).Path
    $savedErrorPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $root = git -C $repo rev-parse --show-prefix 2>$null
    } finally {
        $ErrorActionPreference = $savedErrorPreference
    }
    if ($LASTEXITCODE -ne 0 -or $root) {
        if ($TestConnection) {
            Stop-Sync 'CONFIG' 'Enter a remote repository URL to test a connection without a local Git repository.'
        }
        Stop-Sync 'WINDOWS_REPO' '请在「Git 本地仓库」选择仓库根目录，并检查仓库操作。'
    }
    $gitOptions = @('-C', $repo)
}
if ($ListRemotes) {
    $names = git -C $repo remote
    if ($LASTEXITCODE -ne 0) { throw 'Could not list local Git remotes.' }
    foreach ($name in $names) { Write-Host "LOCAL_REMOTE=$name" }
    exit 0
}
if ($remoteUrl -and $remoteUrl -notmatch '^(?:(?:https|ssh)://[^\s/]+/[^\s]+|(?:[^\s@/:]+@)?[^\s@/:]+:(?!:)[^\s]+)$') {
    throw 'Enter an SSH or HTTPS repository URL.'
}
$remoteTarget = if ($remoteUrl) { $remoteUrl } else { $c.remote }
if (!$remoteTarget) { throw 'Enter a remote URL or name.' }
$githubUsername = ([string]$c.githubUsername).Trim()
if ($githubUsername -and $githubUsername -notmatch '^[a-z0-9](?:[a-z0-9-]{0,37}[a-z0-9])?$') {
    throw 'Enter a valid GitHub login name.'
}
if ($githubUsername -and $remoteUrl -notmatch '^https://github\.com/') {
    throw 'A GitHub login name requires an explicit https://github.com repository URL.'
}
$env:GIT_TERMINAL_PROMPT = '0'
if ($remoteUrl -match '^https://') {
    $uri = [uri]$remoteUrl
    if ($uri.UserInfo -or $uri.Query -or $uri.Fragment) {
        throw 'Use a repository URL without credentials, query parameters, or fragments.'
    }
    if ($uri.Host -ieq 'github.com') {
        git credential-manager --version | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Install Git Credential Manager to use GitHub HTTPS.' }
        $remoteTarget = $remoteUrl.TrimEnd('/')
        if ($githubUsername) {
            $accountUrl = [UriBuilder]::new($remoteTarget)
            $accountUrl.UserName = $githubUsername
            $remoteTarget = $accountUrl.Uri.AbsoluteUri
            $env:GCM_NAMESPACE = "projectsync-github-$($githubUsername.ToLowerInvariant())"
            $env:GCM_CREDENTIAL_STORE = 'wincredman'
        }
        $gitOptions += @('-c', 'credential.helper=', '-c', 'credential.helper=manager', '-c', 'credential.interactive=true')
    }
} elseif ($c.gitSshCommand) {
    $env:GIT_SSH_COMMAND = $c.gitSshCommand
}
if ($InitializeRepository) {
    if (!$remoteUrl -or !$c.branch -or ![IO.Directory]::Exists([string]$c.windowsRepo)) {
        Stop-Sync 'CONFIG' '请选择现有的 Windows 目录，并填写远端仓库地址与目标分支。'
    }
    $directory = Get-Item -LiteralPath $c.windowsRepo -Force
    $repo = $directory.FullName
    if ($repo -notmatch '^[A-Za-z]:\\' -or !$directory.Parent -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Stop-Sync 'CONFIG' '请选择 Windows 磁盘上的项目目录。'
    }
    $metadata = Join-Path $repo '.git'
    $savedErrorPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        git -C $repo rev-parse --git-dir 2>$null | Out-Null
        $existingRepo = $LASTEXITCODE -eq 0
    } finally {
        $ErrorActionPreference = $savedErrorPreference
    }
    $reset = $InitializeRepository -eq 'reset'
    $gitData = Get-Item -LiteralPath $metadata -Force -ErrorAction SilentlyContinue
    if ($reset) {
        if (!$gitData -or !$gitData.PSIsContainer -or ($gitData.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            (Test-Path -LiteralPath (Join-Path $metadata 'commondir'), (Join-Path $metadata 'worktrees'), (Join-Path $metadata 'modules') | Where-Object { $_ })) {
            Stop-Sync 'WINDOWS_REPO' '重置仅支持含普通 .git 目录的独立仓库。请检查工作树、子模块或 Git 元数据链接。'
        }
    } elseif ($existingRepo -or $gitData) {
        Stop-Sync 'WINDOWS_REPO' '此目录已有 Git 元数据，或位于其他 Git 仓库内。请选择仓库根目录或修复现有仓库。'
    }
    $hasFiles = $reset -or [bool](Get-ChildItem -LiteralPath $repo -Force | Select-Object -First 1)
    if (!$reset -and $hasFiles -ne ($InitializeRepository -eq 'existing')) {
        Stop-Sync 'WINDOWS_REPO' '目录内容已变化，请重新确认初始化方式。'
    }
    $remoteName = ([string]$c.remote).Trim()
    if (!$remoteName) { $remoteName = 'origin' }
    git check-ref-format "refs/heads/$($c.branch)" | Out-Null
    if ($LASTEXITCODE -ne 0) { Stop-Sync 'CONFIG' '目标分支名称无效。' }
    git @gitOptions ls-remote --exit-code --heads -- $remoteTarget "refs/heads/$($c.branch)" | Out-Null
    if ($LASTEXITCODE -ne 0) { Stop-Sync 'CONFIG' '无法读取指定远端分支。请检查地址、分支和访问权限。' }
    $clone = @('clone', '--branch', [string]$c.branch, '--origin', $remoteName)
    $destination = $repo
    $staging = $null
    $owned = $null
    $backup = $null
    try {
        if ($hasFiles) {
            $staging = Join-Path $directory.Parent.FullName ('.projectsync-init-' + [Guid]::NewGuid().ToString('N'))
            $owned = New-Item -ItemType Directory -Path $staging
            $destination = $owned.FullName
            $clone += '--no-checkout'
        }
        git @gitOptions @clone -- $remoteTarget $destination
        if ($LASTEXITCODE -ne 0) { throw '无法克隆远端分支。请检查远端地址、分支和访问权限。' }
        if ($remoteTarget -ne $remoteUrl) {
            git -C $destination remote set-url -- $remoteName $remoteUrl
            if ($LASTEXITCODE -ne 0) { throw '无法保存远端地址。' }
        }
        if ($hasFiles) {
            git -C $destination read-tree HEAD
            if ($LASTEXITCODE -ne 0) { throw '无法建立远端分支的暂存区基线。' }
            if ($reset) {
                try {
                    $resetLock = [IO.File]::Open((Join-Path $metadata 'projectsync.lock'),
                        [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                    # Windows rejects the directory rename while this handle is open.
                    $resetLock.Dispose()
                } catch [IO.IOException] {
                    Stop-Sync 'REPOSITORY_BUSY' '此仓库正在被使用，请稍后重试。'
                }
                $backup = Join-Path $directory.Parent.FullName ('.projectsync-git-backup-' + [Guid]::NewGuid().ToString('N'))
                [IO.Directory]::Move($metadata, $backup)
            }
            try {
                [IO.Directory]::Move((Join-Path $destination '.git'), $metadata)
            } catch {
                if ($backup) {
                    try { [IO.Directory]::Move($backup, $metadata) }
                    catch { throw "重置失败，无法自动恢复。旧 Git 数据保存在：$backup" }
                }
                throw
            }
        }
    } finally {
        if ($staging -and $owned -and $owned.FullName -eq $staging -and
            $owned.Parent.FullName -eq $directory.Parent.FullName) {
            Remove-Item -LiteralPath $owned.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    if ($backup) { Write-Host "LOCAL_BACKUP=$backup" }
    Write-Host 'LOCAL_INIT=complete'
    if ($hasFiles) {
        git --no-optional-locks -C $repo -c core.quotepath=false status --short --untracked-files=normal |
            ForEach-Object { Write-Host "LOCAL_CHANGE=$_" }
        if ($LASTEXITCODE -ne 0) { Write-Host 'LOCAL_STATUS=unavailable' }
    }
    exit 0
}
if ($TestConnection) {
    $refs = git @gitOptions ls-remote --heads -- $remoteTarget
    if ($LASTEXITCODE -ne 0) { throw 'Could not read remote branches. Check the remote address and repository access.' }
    foreach ($ref in $refs) {
        if ($ref -cmatch '^[0-9a-f]+\trefs/heads/(.+)$') { Write-Host "REMOTE_BRANCH=$($Matches[1])" }
    }
    exit 0
}

if ($repo -notmatch '^[A-Za-z]:\\' -or $c.wslRepo -notmatch '^/') { throw 'Use an absolute Windows drive path and an absolute WSL path.' }
$pullPaths = if ($c.PSObject.Properties['pullPaths']) {
    @($c.pullPaths)
} elseif ($null -ne $c.pullPath) {
    @($c.pullPath)
}
$pullPaths = @($pullPaths | Where-Object { $null -ne $_ })
foreach ($path in $pullPaths) {
    if ($path -isnot [string] -or [string]::IsNullOrWhiteSpace($path)) { throw 'Each pull directory must be a non-empty string.' }
}
$pullPaths = @($pullPaths | Select-Object -Unique)
function Convert-SyncPath([string]$Path) {
    $value = $Path.Trim()
    if ($value.Length -ge 2 -and $value.StartsWith('"') -and $value.EndsWith('"')) {
        $value = $value.Substring(1, $value.Length - 2)
    }
    $value = $value.Replace('\', '/').TrimEnd('/')
    $comparison = [StringComparison]::Ordinal
    if ($value -match '^/{2,}(?:wsl\.localhost|wsl\$)/([^/]+)(/.*)$') {
        if ($Matches[1] -ine $c.distro) { throw "WSL path belongs to another distribution: $Path" }
        $value = $Matches[2]
        $basePath = ([string]$c.wslRepo).TrimEnd('/') + '/'
    } elseif ($value -match '^[A-Za-z]:/') {
        $basePath = $repo.Replace('\', '/').TrimEnd('/') + '/'
        $comparison = [StringComparison]::OrdinalIgnoreCase
    } elseif ($value.StartsWith('/') -and !$value.StartsWith('//')) {
        $basePath = ([string]$c.wslRepo).TrimEnd('/') + '/'
    } else {
        return $value
    }
    if (!$value.StartsWith($basePath, $comparison)) {
        throw "Path must be inside the configured project directory: $Path"
    }
    return $value.Substring($basePath.Length)
}
$sitePaths = @($c.include | ForEach-Object { Convert-SyncPath $_ })
$exclude = @($c.exclude | Where-Object { $_ } | ForEach-Object { Convert-SyncPath $_ })
foreach ($path in ($sitePaths + $exclude + $pullPaths)) {
    if (!$path -or $path -match '(^/|\\|["<>|:*?\[\]\x00-\x1f]|(^|/)\.{1,2}(/|$)|(^|/)\.git(/|$)|//|/$)') {
        throw "Use a literal repository-relative path with forward slashes: $path"
    }
}
$syncPaths = $sitePaths + @($exclude | ForEach-Object { ":(exclude,literal)$_" })
$suffixes = @($c.excludeSuffixes | Where-Object { $_ } | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ } | Select-Object -Unique)
foreach ($suffix in $suffixes) {
    if ($suffix -cne ':Zone.Identifier' -and $suffix -match '[\\/:*?"<>|\[\]\x00-\x1f]') { throw "Use a literal filename suffix: $suffix" }
}
if ($suffixes -ccontains ':Zone.Identifier') {
    $suffixes += "$([char]0xF03A)Zone.Identifier"
}
$syncPaths += @($suffixes | ForEach-Object { ":(exclude,glob)**/*$_" })
$autoCrlf = git -C $repo config --default false --get core.autocrlf
if ($LASTEXITCODE -ne 0) { throw "Could not read target Git line-ending configuration." }
if (!$Preview -and !$Publish -and !$ExpectedPlan) { throw "Run a preview before applying." }
$commonGitDir = git -C $repo rev-parse --path-format=absolute --git-common-dir
if ($LASTEXITCODE -ne 0 -or !$commonGitDir) { throw 'Could not locate the Windows Git directory.' }
try {
    $repositoryLock = [IO.File]::Open((Join-Path $commonGitDir 'projectsync.lock'),
        [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
} catch [IO.IOException] {
    Stop-Sync 'REPOSITORY_BUSY' 'Another ProjectSync operation is using this Windows Git repository.'
}

try {
git @gitOptions fetch --no-tags -- $remoteTarget "refs/heads/$($c.branch)"
if ($LASTEXITCODE -ne 0) { throw 'Git fetch failed.' }
$commit = git -C $repo rev-parse --verify 'FETCH_HEAD^{commit}'
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve fetched commit.' }
$head = git -C $repo rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Could not read the local branch.' }
$branch = git -C $repo branch --show-current
if ($LASTEXITCODE -ne 0 -or $branch -cne $c.branch) { throw 'Check out the configured Windows branch first.' }

function Assert-ReadyIndex([switch]$AllowStaged) {
    if (!$AllowStaged) {
        git -C $repo diff --cached --quiet
        if ($LASTEXITCODE -ne 0) { throw 'The index has staged changes. Review them before continuing.' }
    }
    foreach ($state in @('MERGE_HEAD','CHERRY_PICK_HEAD','REVERT_HEAD','rebase-merge','rebase-apply','sequencer')) {
        $statePath = git -C $repo rev-parse --path-format=absolute --git-path $state
        if ($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath $statePath)) { throw 'Finish the in-progress Git operation first.' }
    }
}

Assert-ReadyIndex -AllowStaged:$Publish
if ($Publish) {
    if ($ExpectedPlan -notmatch '^([0-9a-f]{40}):([0-9a-f]{40})(?::([0-9a-f]{40}))?$') { throw 'Apply a verified sync first.' }
    $base = $Matches[1]
    $verified = $Matches[2]
    $pending = $Matches[3]
    if ($pending -and $head -eq $pending -and $commit -eq $pending) {
        Write-Host "Already pushed $pending to branch $($c.branch)"
        Write-SyncResult 'PUBLISHED' 'pushed'
        exit 0
    }
    if ($commit -ne $base) { throw 'Remote changed. Review the remote and any pending local commit before continuing.' }
    if ($pending) {
        $parents = git -C $repo rev-list --parents -n 1 $head
        if ($LASTEXITCODE -ne 0 -or $head -ne $pending -or $parents -ne "$pending $base") {
            throw 'The pending commit no longer matches the local branch.'
        }
    } else {
        if ($head -ne $base) { throw 'The local branch changed. Build, preview and apply sync again.' }
        git -C $repo add -A -- .
        if ($LASTEXITCODE -ne 0) { throw 'Could not stage the Windows working tree. Review the Git index.' }
        $changes = @(git -C $repo -c core.quotepath=false diff --cached --name-status)
        if ($LASTEXITCODE -ne 0) { throw 'Could not inspect staged changes.' }
        if ($changes.Count -eq 0) {
            Write-Host 'No changes to publish.'
            Write-SyncResult 'PUBLISHED' 'no_changes'
            exit 0
        }
        Write-Host 'Staged Windows working tree changes:'
        $changes | ForEach-Object { Write-Host $_ }

        if ($CommitSummaryFile) {
            git -C $repo -c i18n.commitEncoding=UTF-8 commit -F $CommitSummaryFile
        } else {
            git -C $repo commit -m 'Update project working tree'
        }
        if ($LASTEXITCODE -ne 0) { throw 'Git commit failed. Review the log and staged changes.' }
        $pending = git -C $repo rev-parse HEAD
        if ($LASTEXITCODE -ne 0) { throw 'Could not read the local commit.' }
    }
    Write-Host "PENDING=${base}:${verified}:$pending"
    git @gitOptions push -- $remoteTarget "${pending}:refs/heads/$($c.branch)"
    if ($LASTEXITCODE -ne 0) { Stop-Sync 'PUSH' 'Push failed. The local commit was saved. You can retry the push.' }
    Write-Host "Pushed $pending to branch $($c.branch)"
    Write-SyncResult 'PUBLISHED' 'pushed'
    exit 0
}

git -C $repo merge-base --is-ancestor $head $commit
if ($LASTEXITCODE -ne 0) { throw 'The local branch cannot fast-forward to the remote. Review local commits first.' }
if ($head -ne $commit) { Write-Host "Windows branch will fast-forward: $head -> $commit" }

$linuxRepo = '/mnt/' + $repo.Substring(0,1).ToLowerInvariant() + '/' + $repo.Substring(3).Replace('\', '/')

$verifiedSnapshot = ''
$expectedHash = ''
if (!$Preview) {
    if ($ExpectedPlan -cnotmatch '^([0-9a-f]{40}):([0-9A-F]{64})$') {
        Stop-Sync 'PREVIEW_EXPIRED' 'Run a new preview to create a verified snapshot.'
    }
    $verifiedSnapshot = $Matches[1]
    $expectedHash = $Matches[2]
}

$build = @'
set -euo pipefail
cd -- "$3" || { echo 'PROJECTSYNC_ERROR=WSL_DIRECTORY'; exit 1; }
windows_repo=$1
remote_commit=$2
build_command=$4
node_bin=$5
target_autocrlf=$6
pull_count=$7
shift 7
pull_paths=("${@:1:pull_count}")
shift "$pull_count"
source_branch=$1
verified_snapshot=$2
build_lease=$3
shift 3
if [[ -n $build_lease ]]; then build_lease=$(wslpath -u "$build_lease"); fi
check_lease() { [[ -z $build_lease || -f $build_lease ]] || exit 130; }
check_lease
site_paths=("$@")
require_source() {
    if [[ ! -e $1 ]]; then
        echo 'PROJECTSYNC_ERROR=SOURCE_MISSING'
        printf 'Missing required source: %s\n' "$2" >&2
        exit 1
    fi
    printf 'Source present: %s\n' "$2"
}
for path in "${site_paths[@]}"; do
    [[ $path == ':('* ]] && continue
    if [[ -z $verified_snapshot ]]; then
        supplied_by_pull=false
        for pull in "${pull_paths[@]}"; do
            if [[ $path == "$pull" || $path == "$pull/"* || $pull == "$path/"* ]]; then
                supplied_by_pull=true
                break
            fi
        done
        if $supplied_by_pull; then continue; fi
    fi
    require_source "$path" "$path"
done
if [[ ! -e .git && -n $verified_snapshot ]]; then
    echo 'PROJECTSYNC_ERROR=PREVIEW_EXPIRED'
    exit 1
fi
if [[ ! -e .git ]]; then
    git init --initial-branch="$source_branch" .
fi
git_dir=$(git rev-parse --absolute-git-dir)
exec 9>"$git_dir/notices-build.lock"
flock -n 9 || { echo 'A site build is already running.' >&2; exit 1; }
if [[ -n $node_bin ]]; then export PATH="$node_bin:$PATH"; fi
tmp=$(mktemp -d)
trap 'rm -rf -- "$tmp"' EXIT

mirror_pull_directories() (
    roots=()
    for path in "${pull_paths[@]}"; do
        covered=false
        for candidate in "${pull_paths[@]}"; do
            if [[ $path == "$candidate/"* ]]; then covered=true; break; fi
        done
        if $covered; then continue; fi
        parent=$path
        while [[ $parent == */* ]]; do
            parent=${parent%/*}
            if [[ -L $parent || ( -e $parent && ! -d $parent ) ]]; then
                printf 'Unsafe WSL mirror parent: %s\n' "$parent" >&2
                exit 1
            fi
        done
        roots+=("$path")
    done

    # Prepare the remote tree separately. The real WSL Git index stays intact.
    stage=$(mktemp -d ./.projectsync-mirror.XXXXXXXX)
    moved=()
    committed=false
    finish_mirror() {
        local result=$? i path recovered=true
        if ! $committed; then
            for ((i=${#moved[@]}-1; i>=0; i--)); do
                path=${moved[i]}
                if [[ ! -e $stage/next/$path ]]; then
                    mv -T -- "$path" "$stage/next/$path" || { recovered=false; continue; }
                fi
                if [[ -e $stage/previous/$i || -L $stage/previous/$i ]]; then
                    mv -T -- "$stage/previous/$i" "$path" || recovered=false
                fi
            done
        fi
        if $recovered; then
            rm -rf --one-file-system -- "$stage"
        else
            printf 'WSL mirror rollback failed. Backups were kept in %s/%s\n' "$PWD" "$stage" >&2
        fi
        return "$result"
    }
    trap finish_mirror EXIT
    trap 'exit 130' INT
    trap 'exit 143' TERM
    mkdir -- "$stage/next" "$stage/previous"
    (
        export GIT_INDEX_FILE="$tmp/mirror-index"
        git read-tree "$remote_commit"
        git --literal-pathspecs ls-files --stage -- "${roots[@]}" > "$tmp/mirror-entries"
        if grep '^160000 ' "$tmp/mirror-entries" >/dev/null; then
            echo 'WSL mirror directories cannot contain Git submodules.' >&2
            exit 1
        fi
        git --literal-pathspecs ls-files -z -- "${roots[@]}" |
            git -c core.symlinks=true checkout-index --prefix="$stage/next/" --stdin -z
    )
    for path in "${roots[@]}"; do
        mkdir -p -- "$stage/next/$path"
    done
    for path in "${site_paths[@]}"; do
        [[ $path == ':('* ]] && continue
        for pull in "${roots[@]}"; do
            if [[ $path == "$pull" || $path == "$pull/"* ]]; then
                require_source "$stage/next/$path" "$path"
                break
            fi
            if [[ $pull == "$path/"* && ! -e $path ]]; then
                require_source "$stage/next/$path" "$path"
                break
            fi
        done
    done

    for path in "${roots[@]}"; do
        check_lease
        mkdir -p -- "$(dirname -- "$path")"
        i=${#moved[@]}
        moved+=("$path")
        if [[ -e $path || -L $path ]]; then
            mv -T -- "$path" "$stage/previous/$i"
        fi
        mv -T -- "$stage/next/$path" "$path"
    done
    committed=true
)

if [[ -z $verified_snapshot ]]; then
    git -c safe.directory="$windows_repo" fetch --no-tags "$windows_repo" "$remote_commit"
    for path in "${pull_paths[@]}"; do
        if [[ $(git cat-file -t "$remote_commit:$path" 2>/dev/null) != tree ]]; then
            printf 'Missing remote directory: %s\n' "$path" >&2
            exit 1
        fi
    done
    if (( pull_count > 0 )) && [[ . -ef $windows_repo ]]; then
        echo 'WSL mirror requires a repository separate from the Windows repository.' >&2
        exit 1
    fi
    if ! git rev-parse --verify -q 'HEAD^{commit}' >/dev/null; then
        if ! git symbolic-ref -q HEAD >/dev/null || ! staged_tree=$(git write-tree); then
            echo 'PROJECTSYNC_ERROR=WSL_INDEX'
            exit 1
        fi
        if ! (
            export GIT_INDEX_FILE="$tmp/initial-index"
            git read-tree "$staged_tree" &&
            git add -u &&
            git -c core.quotepath=false diff --cached --name-status "$staged_tree" &&
            git diff --cached --quiet "$staged_tree"
        ); then
            echo 'PROJECTSYNC_ERROR=WSL_INDEX'
            exit 1
        fi
        git reset --mixed --quiet "$remote_commit"
        echo 'Initialized WSL Git history from the configured remote branch.'
    fi
    if (( pull_count > 0 )); then
        mirror_pull_directories
    fi

    # Build with the current WSL sources.
    if [[ -z $build_lease ]]; then
        CI=1 NO_COLOR=1 bash -e -o pipefail -c "$build_command" 9>&- || {
            echo 'PROJECTSYNC_ERROR=BUILD'
            exit 1
        }
    else
        check_lease
        CI=1 NO_COLOR=1 setsid bash -e -o pipefail -c "$build_command" </dev/null 9>&- &
        build_pid=$!
        while kill -0 "$build_pid" 2>/dev/null; do
            if [[ ! -f $build_lease ]]; then
                kill -TERM -- "-$build_pid" 2>/dev/null || kill -TERM "$build_pid" 2>/dev/null || true
                sleep 1
                kill -KILL -- "-$build_pid" 2>/dev/null || true
                wait "$build_pid" || true
                exit 130
            fi
            sleep 0.2
        done
        wait "$build_pid" || { echo 'PROJECTSYNC_ERROR=BUILD'; exit 1; }
        check_lease
    fi
fi

# Use the target's Git line-ending policy so the snapshot matches what Windows stages.
source_repo=$PWD
source_tree=$(
    export GIT_INDEX_FILE="$tmp/index"
    git read-tree HEAD
    git ls-files -z --cached --others -- "${site_paths[@]}" > "$tmp/paths"
    if [[ -s "$tmp/paths" ]]; then
        LC_ALL=C git -c core.autocrlf="$target_autocrlf" --literal-pathspecs add -f -A --pathspec-from-file="$tmp/paths" --pathspec-file-nul
    fi
    git write-tree
)
if [[ -z $verified_snapshot ]]; then
    snapshot=$(git -c user.name='Site build' -c user.email='site-build@localhost' commit-tree "$source_tree" -p HEAD -m 'Verified site source snapshot')
    git -c safe.directory="$windows_repo" -C "$windows_repo" fetch --no-tags --no-write-fetch-head "$source_repo" "$snapshot"
else
    snapshot=$verified_snapshot
fi
printf 'SITE_SNAPSHOT=%s\n' "$snapshot"
(
    export GIT_INDEX_FILE="$tmp/target-index"
    git_target=(git -c core.filemode=false -c core.autocrlf="$target_autocrlf" -c safe.directory="$windows_repo" -C "$windows_repo")
    "${git_target[@]}" read-tree HEAD
    "${git_target[@]}" ls-files -z --cached --others -- "${site_paths[@]}" > "$tmp/target-paths"
    if [[ -s "$tmp/target-paths" ]]; then
        LC_ALL=C "${git_target[@]}" --literal-pathspecs add -f -A --pathspec-from-file="$tmp/target-paths" --pathspec-file-nul
    fi
    target_tree=$("${git_target[@]}" write-tree)
    if [[ $("${git_target[@]}" rev-parse --verify "$snapshot^{tree}") != "$source_tree" ]]; then
        echo 'PROJECTSYNC_ERROR=PREVIEW_EXPIRED'
        exit 1
    fi
    if [[ -z $verified_snapshot ]]; then
        "${git_target[@]}" -c core.quotepath=false diff --name-status --no-renames "$target_tree" "$source_tree" -- "${site_paths[@]}"
    fi
    printf 'SITE_TREES=%s:%s\n' "$source_tree" "$target_tree"
)
'@

$snapshot = $null
$trees = $null
$oldEncoding = $OutputEncoding
$oldConsoleEncoding = [Console]::OutputEncoding
try {
    $OutputEncoding = [Text.UTF8Encoding]::new($false)
    [Console]::OutputEncoding = $OutputEncoding
    $argsForBash = @($linuxRepo, $commit, $c.wslRepo, $c.buildCommand, [string]$c.nodeBin, $autoCrlf, [string]$pullPaths.Count) + $pullPaths + @($c.branch, $verifiedSnapshot, $BuildLease) + $syncPaths
    $quoted = ($argsForBash | ForEach-Object { "'" + $_.Replace("'", "'\''") + "'" }) -join ' '
    $build | wsl.exe -d $c.distro -u $c.wslUser -- bash -c "sed '1s/^\xEF\xBB\xBF//;s/\r$//' | bash -s -- $quoted 2>&1" | ForEach-Object {
        $_ = $_.TrimEnd([char]13)
        if ($_ -cmatch '^PROJECTSYNC_ERROR=([A-Z_]+)$') { Write-SyncResult 'PROJECTSYNC_ERROR' $Matches[1] }
        elseif ($_ -match '^SITE_SNAPSHOT=([0-9a-f]{40})$') { $snapshot = $Matches[1] }
        elseif ($_ -match '^SITE_TREES=([0-9a-f]{40}:[0-9a-f]{40})$') { $trees = $Matches[1] }
        elseif ($_ -cnotmatch "^warning: in the working copy of '.+', (?:LF will be replaced by CRLF|CRLF will be replaced by LF) the next time Git touches it$") {
            Write-Host $_
        }
    }
    if ($LASTEXITCODE -ne 0 -or -not $snapshot -or -not $trees) { Write-Host 'WSL build or source snapshot failed. Windows sync stopped.'; exit 1 }
} finally {
    [Console]::OutputEncoding = $oldConsoleEncoding
    $OutputEncoding = $oldEncoding
}

$protect = @()
foreach ($path in $exclude) { $protect += @('-e', "/$path", '-e', "/$path/") }
foreach ($suffix in $suffixes) { $protect += @('-e', "*$suffix") }
$cleanup = @()
if ($c.cleanUntracked -eq $true) {
    $cleanup = @(git -C $repo -c core.quotepath=false clean -nd @protect)
    if ($LASTEXITCODE -ne 0) { throw 'Windows cleanup preview failed.' }
    if ($Preview) {
        $cleanup | ForEach-Object { Write-Host ('CLEANUP=' + ($_ -creplace '^Would remove ', '')) }
    } elseif (!$AllowCleanup) {
        Stop-Sync 'CLEANUP_CONFIRMATION' 'Windows untracked-file cleanup requires explicit confirmation.'
    }
}
$planText = ConvertTo-Json -Compress -Depth 8 -InputObject @(
    $head, $commit, $snapshot, $trees, $repo, $remoteTarget, $c.branch, $c.distro, $c.wslUser,
    $c.wslRepo, $c.buildCommand, $c.nodeBin, $autoCrlf, ($c.cleanUntracked -eq $true),
    $pullPaths, $syncPaths, $cleanup
)
$sha = [Security.Cryptography.SHA256]::Create()
try { $plan = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($planText))).Replace('-','') }
finally { $sha.Dispose() }
if ($Preview) {
    Write-SyncResult 'PLAN' "${snapshot}:$plan"
    exit 0
}
if ($plan -cne $expectedHash) { Stop-Sync 'PREVIEW_EXPIRED' 'Source, target, or remote changed. Run a new preview.' }
$currentBranch = git -C $repo branch --show-current
if ($LASTEXITCODE -ne 0 -or $currentBranch -cne $c.branch) { throw 'The checked-out branch changed. Run a new preview.' }
$currentHead = git -C $repo rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $currentHead -ne $head) { throw 'The local branch changed. Run a new preview.' }
Assert-ReadyIndex
if ($head -ne $commit) {
    git -C $repo merge --ff-only $commit
    if ($LASTEXITCODE -ne 0) { throw 'Fast-forward failed. Review the Windows working tree before applying sync.' }
}
if ($c.cleanUntracked -eq $true) {
    $currentCleanup = @(git -C $repo -c core.quotepath=false clean -nd @protect)
    if ($LASTEXITCODE -ne 0 -or ($currentCleanup -join [char]0) -cne ($cleanup -join [char]0)) {
        Stop-Sync 'PREVIEW_EXPIRED' 'The cleanup candidates changed. Create a new preview before cleaning.'
    }
    git -C $repo clean -fd @protect
    if ($LASTEXITCODE -ne 0) { throw 'Windows untracked-file cleanup failed.' }
}
git -C $repo restore --source=$snapshot --worktree -- @syncPaths
if ($LASTEXITCODE -ne 0) { throw 'Windows site source sync failed.' }
Write-Host "Synced full site sources verified with input from $commit"
Write-SyncResult 'SYNCED' "${commit}:$snapshot"
exit 0
} finally {
    $repositoryLock.Dispose()
}
