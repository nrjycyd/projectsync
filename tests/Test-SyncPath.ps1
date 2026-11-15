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
Write-Output 'BuildSync syntax and PowerShell path checks passed.'
