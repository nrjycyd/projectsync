param(
    [string]$Config = (Join-Path $PSScriptRoot 'build-sync.json'),
    [switch]$Preview,
    [switch]$Publish,
    [switch]$TestConnection,
    [switch]$ListRemotes,
    [string]$ExpectedPlan,
    [string]$CommitSummaryFile
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
function Stop-Sync([string]$Code, [string]$Detail) {
    Write-Host "PROJECTSYNC_ERROR=$Code"
    Write-Host $Detail
    exit 1
}
$c = Get-Content -LiteralPath $Config -Raw -Encoding UTF8 | ConvertFrom-Json
if (!$ListRemotes -and !$TestConnection) {
    $missing = @('windowsRepo', 'wslRepo', 'distro', 'wslUser', 'buildCommand', 'branch', 'include' |
        Where-Object { !$c.$_ })
    if ($missing.Count -gt 0) { Stop-Sync 'CONFIG' "Missing fields: $($missing -join ', ')" }
}
$repo = (Resolve-Path -LiteralPath $c.windowsRepo).Path
$root = git -C $repo rev-parse --show-prefix
if ($LASTEXITCODE -ne 0 -or $root) { throw 'windowsRepo must point to the root of a Git working tree.' }
if ($ListRemotes) {
    $names = git -C $repo remote
    if ($LASTEXITCODE -ne 0) { throw 'Could not list local Git remotes.' }
    foreach ($name in $names) { Write-Host "LOCAL_REMOTE=$name" }
    exit 0
}
$remoteUrl = ([string]$c.remoteUrl).Trim()
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
$gitOptions = @('-C', $repo)
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
$suffixMatches = @($suffixes | ForEach-Object { ":(glob)**/*$_" })
$syncPaths += @($suffixes | ForEach-Object { ":(exclude,glob)**/*$_" })
$outsideSyncPaths = @($sitePaths | ForEach-Object { ":(exclude,literal)$_" })
$autoCrlf = git -C $repo config --default false --get core.autocrlf
if ($LASTEXITCODE -ne 0) { throw "Could not read target Git line-ending configuration." }
if (!$Preview -and !$Publish -and !$ExpectedPlan) { throw "Run a preview before applying." }
git @gitOptions fetch --no-tags -- $remoteTarget "refs/heads/$($c.branch)"
if ($LASTEXITCODE -ne 0) { throw 'Git fetch failed.' }
$commit = git -C $repo rev-parse --verify 'FETCH_HEAD^{commit}'
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve fetched commit.' }
$head = git -C $repo rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Could not read the local branch.' }
$branch = git -C $repo branch --show-current
if ($LASTEXITCODE -ne 0 -or $branch -cne $c.branch) { throw 'Check out the configured Windows branch first.' }

function Assert-ReadyIndex {
    git -C $repo diff --cached --quiet
    if ($LASTEXITCODE -ne 0) { throw 'The index has staged changes. Review them before continuing.' }
    foreach ($state in @('MERGE_HEAD','CHERRY_PICK_HEAD','REVERT_HEAD','rebase-merge','rebase-apply','sequencer')) {
        $statePath = git -C $repo rev-parse --path-format=absolute --git-path $state
        if ($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath $statePath)) { throw 'Finish the in-progress Git operation first.' }
    }
}

Assert-ReadyIndex
if ($Publish) {
    if ($ExpectedPlan -notmatch '^([0-9a-f]{40}):([0-9a-f]{40})(?::([0-9a-f]{40}))?$') { throw 'Apply a verified sync first.' }
    $base = $Matches[1]
    $verified = $Matches[2]
    $pending = $Matches[3]
    if ($pending -and $head -eq $pending -and $commit -eq $pending) {
        Write-Host "Already pushed $pending to branch $($c.branch)"
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
        $paths = @(git -C $repo ls-files -z --cached --others -- @syncPaths)
        if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate files to stage.' }
        $pathList = [IO.Path]::GetTempFileName()
        try {
            [IO.File]::WriteAllText($pathList, ($paths -join ''), [Text.UTF8Encoding]::new($false))
            $stageStatus = 0
            if ((Get-Item -LiteralPath $pathList).Length -gt 0) {
                git -C $repo --literal-pathspecs add -f -A --pathspec-from-file=$pathList --pathspec-file-nul
                $stageStatus = $LASTEXITCODE
            }
        } finally { Remove-Item -LiteralPath $pathList }
        $differences = @(git -C $repo -c core.quotepath=false diff --cached --name-status $verified -- @syncPaths)
        if ($stageStatus -ne 0 -or $LASTEXITCODE -ne 0 -or $differences.Count -gt 0) {
            git -C $repo restore --staged -- @syncPaths
            if ($LASTEXITCODE -ne 0) { throw 'Staging verification failed. Review the Git index.' }
            $differences | ForEach-Object { Write-Host $_ }
            throw 'Could not stage the verified sources. Review the paths above, then run a new preview and sync.'
        }
        git -C $repo diff --cached --quiet
        if ($LASTEXITCODE -eq 0) { Write-Host 'No changes to publish.'; exit 0 }
        if ($LASTEXITCODE -ne 1) { throw 'Could not inspect staged changes.' }

        if ($CommitSummaryFile) {
            git -C $repo -c i18n.commitEncoding=UTF-8 commit -F $CommitSummaryFile
        } else {
            git -C $repo commit -m 'Update verified project sources'
        }
        if ($LASTEXITCODE -ne 0) { throw 'Git commit failed. Review the log and staged changes.' }
        $pending = git -C $repo rev-parse HEAD
        if ($LASTEXITCODE -ne 0) { throw 'Could not read the local commit.' }
    }
    git -C $repo diff --quiet $pending $verified -- @syncPaths
    if ($LASTEXITCODE -ne 0) { throw 'The local commit does not match the verified sources.' }
    git -C $repo diff --quiet $base $pending -- . @outsideSyncPaths
    if ($LASTEXITCODE -ne 0) { throw 'The local commit contains changes outside the sync scope.' }
    if ($exclude.Count -gt 0) {
        git -C $repo diff --quiet $base $pending -- @exclude
        if ($LASTEXITCODE -ne 0) { throw 'The local commit contains excluded changes.' }
    }
    if ($suffixMatches.Count -gt 0) {
        git -C $repo diff --quiet $base $pending -- @suffixMatches
        if ($LASTEXITCODE -ne 0) { throw 'The local commit contains files with excluded suffixes.' }
    }
    Write-Host "PENDING=${base}:${verified}:$pending"
    git @gitOptions push -- $remoteTarget "${pending}:refs/heads/$($c.branch)"
    if ($LASTEXITCODE -ne 0) { Stop-Sync 'PUSH' 'Push failed. The local commit was saved. You can retry the push.' }
    Write-Host "Pushed $pending to branch $($c.branch)"
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
shift 2
site_paths=("$@")
for path in "${site_paths[@]}"; do
    [[ $path == ':('* ]] && continue
    if [[ ! -e $path ]]; then
        echo 'PROJECTSYNC_ERROR=SOURCE_MISSING'
        printf 'Missing required source: %s\n' "$path" >&2
        exit 1
    fi
    printf 'Source present: %s\n' "$path"
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
if [[ -z $verified_snapshot ]]; then
    git -c safe.directory="$windows_repo" fetch --no-tags "$windows_repo" "$remote_commit"
    for path in "${pull_paths[@]}"; do
        if [[ $(git cat-file -t "$remote_commit:$path" 2>/dev/null) != tree ]]; then
            printf 'Missing remote directory: %s\n' "$path" >&2
            exit 1
        fi
    done
    if ! git rev-parse --verify -q 'HEAD^{commit}' >/dev/null; then
        if ! git symbolic-ref -q HEAD >/dev/null || ! staged_tree=$(git write-tree); then
            echo 'PROJECTSYNC_ERROR=WSL_INDEX'
            exit 1
        fi
        if ! (
            export GIT_INDEX_FILE="$tmp/initial-index"
            git read-tree "$staged_tree" &&
            git add -u -- . &&
            git -c core.quotepath=false diff --cached --name-status "$staged_tree" &&
            git diff --cached --quiet "$staged_tree"
        ); then
            echo 'PROJECTSYNC_ERROR=WSL_INDEX'
            exit 1
        fi
        git reset --mixed "$remote_commit"
        echo 'Initialized WSL Git history from the configured remote branch.'
    fi
    if (( pull_count > 0 )); then
        git --literal-pathspecs diff --cached --quiet HEAD -- "${pull_paths[@]}" || {
            echo 'WSL pull directories contain staged edits.' >&2
            exit 1
        }
        pull_state=$(printf '%s\0' "${pull_paths[@]}" | git hash-object --stdin)
        pull_ref="refs/projectsync/pulled/$pull_state"
        if [[ -n $(git --literal-pathspecs status --porcelain --untracked-files=all -- "${pull_paths[@]}") ]]; then
            (
                export GIT_INDEX_FILE="$tmp/pull-index"
                git read-tree HEAD
                git --literal-pathspecs add -A -- "${pull_paths[@]}"
                previous=$(git rev-parse -q --verify "$pull_ref" || true)
                if git --literal-pathspecs diff --cached --quiet "$remote_commit" -- "${pull_paths[@]}"; then exit 0; fi
                if [[ -n $previous ]] && git --literal-pathspecs diff --cached --quiet "$previous" -- "${pull_paths[@]}"; then exit 0; fi
                echo 'WSL pull directories contain local changes. Review them before fetching:' >&2
                git --literal-pathspecs diff --cached --name-status "${previous:-HEAD}" -- "${pull_paths[@]}" >&2
                exit 1
            )
        fi
        git --literal-pathspecs restore --source="$remote_commit" --worktree -- "${pull_paths[@]}"
        git update-ref "$pull_ref" "$remote_commit"
    fi

    # Build with the current WSL sources.
    CI=1 NO_COLOR=1 bash -e -o pipefail -c "$build_command" || {
        echo 'PROJECTSYNC_ERROR=BUILD'
        exit 1
    }
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
    $argsForBash = @($linuxRepo, $commit, $c.wslRepo, $c.buildCommand, [string]$c.nodeBin, $autoCrlf, [string]$pullPaths.Count) + $pullPaths + @($c.branch, $verifiedSnapshot) + $syncPaths
    $quoted = ($argsForBash | ForEach-Object { "'" + $_.Replace("'", "'\''") + "'" }) -join ' '
    $build | wsl.exe -d $c.distro -u $c.wslUser -- bash -c "sed '1s/^\xEF\xBB\xBF//;s/\r$//' | bash -s -- $quoted 2>&1" | ForEach-Object {
        $_ = $_.TrimEnd([char]13)
        if ($_ -match '^SITE_SNAPSHOT=([0-9a-f]{40})$') { $snapshot = $Matches[1] }
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
    $cleanup | ForEach-Object { Write-Host $_ }
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
    Write-Host "PLAN=${snapshot}:$plan"
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
    git -C $repo clean -fd @protect
    if ($LASTEXITCODE -ne 0) { throw 'Windows untracked-file cleanup failed.' }
}
git -C $repo restore --source=$snapshot --worktree -- @syncPaths
if ($LASTEXITCODE -ne 0) { throw 'Windows site source sync failed.' }
Write-Host "Synced full site sources verified with input from $commit"
Write-Host "SYNCED=${commit}:$snapshot"
exit 0
