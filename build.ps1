<#
.SYNOPSIS
  Test, publish and package the Zinwe Windows agent.

.EXAMPLE
  .\build.ps1                    # tests + publish + artifacts\installer\ZinweAgent.msi
  .\build.ps1 -Version 0.2.0
  .\build.ps1 -SkipInstaller     # publish only
  .\build.ps1 -Version 1.0.0 -BackendUrl https://dev.backend.zinwe.io   # a dev-server build

.PARAMETER BackendUrl
  Bakes this backend into the service's appsettings.json, so a plain double-click install (and every
  one-click update) talks to it with no BACKEND_URL property. Default: appsettings.json as committed
  (production). Must be https.
#>
param(
    [string]$Version = "0.1.0",
    [string]$BackendUrl = "",
    [switch]$SkipTests,
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

function Invoke-Step([string]$Name, [scriptblock]$Body) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Body
    if ($LASTEXITCODE -ne 0) { throw "$Name failed (exit $LASTEXITCODE)." }
}

$publish = Join-Path $PSScriptRoot "artifacts\publish"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

if (-not $SkipTests) {
    # One test project at a time: testing the whole .slnx builds the WPF tray twice in parallel (once for the
    # solution, once for Zinwe.Agent.Tray.Tests) and the two markup-compile passes race on obj\ (CS2001/BG1002).
    foreach ($testProject in Get-ChildItem tests -Filter *.Tests.csproj -Recurse) {
        Invoke-Step "Test $($testProject.BaseName)" { dotnet test $testProject.FullName -c Release }
    }
}

Invoke-Step "Publish service" {
    dotnet publish src\Zinwe.Agent.Service -c Release -p:Version=$Version -o "$publish\service"
}
if ($BackendUrl) {
    if ($BackendUrl -notmatch '^https://[^/\s]+/?$') { throw "BackendUrl must be https://host" }
    $settings = Join-Path $publish "service\appsettings.json"
    $json = Get-Content $settings -Raw | ConvertFrom-Json
    $json.Agent.BackendBaseUrl = $BackendUrl.TrimEnd("/")
    [IO.File]::WriteAllText($settings, ($json | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding $false))
    Write-Host "==> Backend baked in: $($json.Agent.BackendBaseUrl)" -ForegroundColor Cyan
}
Invoke-Step "Publish tray" {
    dotnet publish src\Zinwe.Agent.Tray -c Release -p:Version=$Version -o "$publish\tray"
}

if (-not $SkipInstaller) {
    Invoke-Step "Build MSI" {
        dotnet build installer\Zinwe.Agent.Installer.wixproj -c Release -p:ProductVersion=$Version -o artifacts\installer
    }
    Write-Host "MSI: $(Join-Path $PSScriptRoot 'artifacts\installer\ZinweAgent.msi')" -ForegroundColor Green
    Write-Host "Unsigned build -- sign before giving it to anyone (see README, 'Code signing')." -ForegroundColor Yellow
}
