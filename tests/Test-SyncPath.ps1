$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$script = (Resolve-Path (Join-Path $PSScriptRoot '../src/ProjectSync.Core/Scripts/BuildSync.ps1')).Path
$ast = [Management.Automation.Language.Parser]::ParseFile($script, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors -join [Environment]::NewLine) }
$function = $ast.Find({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Convert-SyncPath'
}, $true)
. ([scriptblock]::Create($function.Extent.Text))

$repo = 'D:\项目 空格'
$c = @{ distro = 'Ubuntu'; wslRepo = '/home/user/项目 空格' }
foreach ($path in @('文档\a.txt', '"d:/项目 空格/文档/a.txt"',
    '/home/user/项目 空格/文档/a.txt', '\\wsl.localhost\Ubuntu\home\user\项目 空格\文档\a.txt')) {
    if ((Convert-SyncPath $path) -cne '文档/a.txt') { throw "Unexpected normalization: $path" }
}
foreach ($path in @('D:\项目 空格2\a', '/home/user/outside/a', '\\wsl$\Other\home\user\项目 空格\文档')) {
    $rejected = $false
    try { $null = Convert-SyncPath $path }
    catch { $rejected = $true }
    if (!$rejected) { throw "Expected rejection: $path" }
}
$originalPath = $env:Path
$testDir = Join-Path ([IO.Path]::GetTempPath()) ('projectsync-no-git-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $testDir | Out-Null
try {
    $config = Join-Path $testDir 'config.json'
    $result = Join-Path $testDir 'result.json'
    '{"remoteUrl":"https://example.invalid/repo","windowsRepo":""}' | Set-Content -LiteralPath $config -Encoding UTF8
    $env:Path = $testDir
    foreach ($mode in '-ListRemotes', '-TestConnection', '-InitializeRepository') {
        $arguments = @($mode)
        if ($mode -eq '-InitializeRepository') { $arguments += 'empty' }
        $output = & (Join-Path $PSHOME 'powershell.exe') -NoProfile -File $script -Config $config -ResultFile $result @arguments
        if ($LASTEXITCODE -ne 1) { throw "Expected missing Git failure for ${mode}: $output" }
        $payload = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
        if ($payload.kind -cne 'PROJECTSYNC_ERROR' -or $payload.value -cne 'WINDOWS_GIT_MISSING') {
            throw "Unexpected missing Git result for ${mode}: $output"
        }
        Remove-Item -LiteralPath $result
    }
} finally {
    $env:Path = $originalPath
    Remove-Item -LiteralPath (Join-Path $testDir 'config.json'), (Join-Path $testDir 'result.json') -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $testDir
}
Write-Output 'BuildSync syntax, PowerShell path, and Windows Git prerequisite checks passed.'
# The last child process deliberately exits 1. Report the test script's own success to CI.
exit 0
