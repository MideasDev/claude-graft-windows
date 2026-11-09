# Builds Claude Graft for Windows into dist\.
#
#   .\build.ps1              framework-dependent single file (needs the .NET 9 Desktop Runtime)
#   .\build.ps1 -SelfContained   carries its own runtime, larger but needs nothing installed
#   .\build.ps1 -Zip         also packs dist\ClaudeGraft-<version>-win-x64.zip
#
# The suite runs first; a failing check stops the build.
param(
    [switch]$SelfContained,
    [switch]$Zip,
    [string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

& "$PSScriptRoot\test.ps1"
if ($LASTEXITCODE -ne 0) { throw 'The suite failed; nothing was built.' }

$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version
$out = Join-Path $PSScriptRoot 'dist'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }

dotnet publish src\ClaudeGraft\ClaudeGraft.csproj -c Release -r $Runtime `
    --self-contained:$($SelfContained.IsPresent) `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

if ($Zip) {
    $flavour = if ($SelfContained) { "$Runtime-selfcontained" } else { $Runtime }
    $archive = Join-Path $out "ClaudeGraft-$version-$flavour.zip"
    Compress-Archive -Path (Join-Path $out 'ClaudeGraft.exe'), LICENSE, README.md -DestinationPath $archive -Force
    Write-Host "Packed $archive"
}
Write-Host "Built $out\ClaudeGraft.exe ($version)"
