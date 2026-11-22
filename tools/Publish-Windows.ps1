param(
    [Parameter(Mandatory)]
    [string]$OutputDirectory,
    [string]$ExpectedTag
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$app = Join-Path $root 'src/SiteLift.App/SiteLift.App.csproj'
[xml]$project = Get-Content -LiteralPath $app -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -cnotmatch '^\d+\.\d+\.\d+$') { throw "Invalid application version: $version" }
if ($ExpectedTag -and $ExpectedTag -cne "v$version") {
    throw "Tag $ExpectedTag does not match application version v$version."
}

$output = [IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($OutputDirectory)).FullName
$staging = [IO.Directory]::CreateDirectory((Join-Path $output ('.sitelift-package-' + [Guid]::NewGuid().ToString('N'))))
$package = Join-Path $staging.FullName 'SiteLift'
$name = "SiteLift-v$version-win-x64.zip"
$archive = Join-Path $staging.FullName $name
try {
    dotnet publish $app --configuration Release --runtime win-x64 --self-contained false --output $package
    if ($LASTEXITCODE -ne 0) { throw 'SiteLift publish failed.' }

    Get-ChildItem -LiteralPath $package -Recurse -File -Filter '*.pdb' | ForEach-Object {
        Remove-Item -LiteralPath $_.FullName
    }
    Compress-Archive -LiteralPath $package -DestinationPath $archive
    $verified = Join-Path $staging.FullName 'verified'
    Expand-Archive -LiteralPath $archive -DestinationPath $verified
    $roots = @(Get-ChildItem -LiteralPath $verified -Force)
    if ($roots.Count -ne 1 -or !$roots[0].PSIsContainer -or $roots[0].Name -cne 'SiteLift') {
        throw 'Unexpected ZIP root.'
    }
    $folder = $roots[0].FullName
    $expected = @(
        'SiteLift.App.exe'
        'SiteLift.App.dll'
        'SiteLift.Core.dll'
        'Wpf.Ui.dll'
        'Wpf.Ui.Abstractions.dll'
        'licenses/WPF-UI-LICENSE.md'
        'licenses/WPF-UI-ThirdPartyNotices.txt'
        'licenses/DevelopmentToolLogos.md'
        'SiteLift.App.deps.json'
        'SiteLift.App.runtimeconfig.json'
    )
    $actual = @(Get-ChildItem -LiteralPath $folder -Recurse -File -Force | ForEach-Object {
        [IO.Path]::GetRelativePath($folder, $_.FullName).Replace('\', '/')
    })
    if ($actual.Count -ne $expected.Count -or (Compare-Object $expected $actual -CaseSensitive)) {
        throw "Unexpected package contents: $($actual -join ', ')"
    }
    $assembly = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $folder 'SiteLift.App.dll'))
    if ($assembly.Version.ToString(3) -cne $version) { throw 'Packaged assembly version does not match project.' }
    $destination = Join-Path $output $name
    Move-Item -LiteralPath $archive -Destination $destination -Force
    $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS') -Value "$hash  $name" -Encoding ascii
    Write-Output $destination
} finally {
    $cleanup = Get-Item -LiteralPath $staging.FullName -Force
    if ($cleanup.Parent.FullName -eq $output -and $cleanup.Name -match '^\.sitelift-package-[0-9a-f]{32}$' -and
        !($cleanup.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Remove-Item -LiteralPath $cleanup.FullName -Recurse -Force
    }
}
