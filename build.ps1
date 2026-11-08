# Builds, tests and publishes the portable X04G Windows ROM Toolkit.
# Usage: .\build.ps1 [-SkipTests] [-FrameworkDependent]
param([switch]$SkipTests, [switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "The .NET 8 SDK is required: https://dotnet.microsoft.com/download/dotnet/8.0 (or: winget install Microsoft.DotNet.SDK.8)"
}

dotnet build X04G_Windows_Toolkit.sln -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

if (-not $SkipTests) {
    dotnet run --project tests\X04G.Core.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
}

$out = Join-Path $PSScriptRoot 'publish'
if (Test-Path (Join-Path $out 'X04GToolkit.exe')) {
    $out = Join-Path $PSScriptRoot ("publish_" + (Get-Date -Format 'yyyyMMdd_HHmmss'))
}
$sc = if ($FrameworkDependent) { '--no-self-contained' } else { '--self-contained' }
dotnet publish src\Application\X04G.Toolkit.App.csproj -c Release -r win-x64 $sc `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $out
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }
dotnet publish src\Cli\X04G.Cli.csproj -c Release -r win-x64 $sc -p:PublishSingleFile=true -o (Join-Path $out 'cli')
Write-Host "Done: $out\X04GToolkit.exe"
