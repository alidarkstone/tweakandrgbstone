# Builds dist\rgbstone.exe (single portable exe, needs only the .NET Framework 4.8.1 that ships with Windows 10/11).
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dotnet = Join-Path $root ".tools\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) {
    New-Item -ItemType Directory -Force (Join-Path $root ".tools") | Out-Null
    Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile (Join-Path $root ".tools\dotnet-install.ps1") -UseBasicParsing
    & (Join-Path $root ".tools\dotnet-install.ps1") -Channel 8.0 -InstallDir (Join-Path $root ".tools\dotnet") -NoPath
}
if (-not (Test-Path (Join-Path $root "assets\rgbstone.ico"))) { & (Join-Path $root "tools\make-logo.ps1") }

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
& $dotnet build (Join-Path $root "src\rgbstone\rgbstone.csproj") -c Release -o (Join-Path $root "build\bin") --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "build failed" }
New-Item -ItemType Directory -Force (Join-Path $root "dist") | Out-Null
Copy-Item (Join-Path $root "build\bin\rgbstone.exe") (Join-Path $root "dist\rgbstone.exe") -Force
Get-Item (Join-Path $root "dist\rgbstone.exe") | Select-Object FullName, Length
