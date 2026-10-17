<#
.SYNOPSIS
  Test, publish and package the Zinwe Windows agent.

.EXAMPLE
  .\build.ps1                    # tests + publish + artifacts\installer\ZinweAgent.msi
  .\build.ps1 -Version 0.2.0
  .\build.ps1 -SkipInstaller     # publish only
  .\build.ps1 -Version 1.0.0 -BackendUrl https://dev.backend.zinwe.io   # a dev-server build
  .\build.ps1 -Version 1.0.2 -BackendUrl https://dev.backend.zinwe.io -CertThumbprint 6FFAC695...   # signed

.PARAMETER BackendUrl
  Bakes this backend into the service's appsettings.json, so a plain double-click install (and every
  one-click update) talks to it with no BACKEND_URL property. Default: appsettings.json as committed
  (production). Must be https.

.PARAMETER CertThumbprint
  Authenticode-signs our own binaries (Zinwe.*.exe / Zinwe.*.dll, before they go into the MSI) and then the
  MSI, with the code-signing certificate that has this thumbprint in Cert:\CurrentUser\My or
  Cert:\LocalMachine\My. Dev builds use the self-signed "Zinwe Dev (SKYTECHNO)" certificate (only trusted on
  PCs where its .cer was imported); release builds use the purchased certificate. Omitted = unsigned.

.PARAMETER TimestampUrl
  RFC 3161 timestamp server, so signatures stay valid after the certificate expires.
#>
param(
    [string]$Version = "0.1.0",
    [string]$BackendUrl = "",
    [string]$CertThumbprint = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com",
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

$signingCert = $null
if ($CertThumbprint) {
    $thumb = ($CertThumbprint -replace '\s', '').ToUpperInvariant()
    $signingCert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -CodeSigningCert |
        Where-Object { $_.Thumbprint -eq $thumb -and $_.HasPrivateKey } | Select-Object -First 1
    if (-not $signingCert) { throw "No code-signing certificate with a private key and thumbprint $thumb." }
    if ($signingCert.NotAfter -lt (Get-Date)) { throw "Signing certificate expired on $($signingCert.NotAfter)." }
}

function Invoke-Sign([string[]]$Files) {
    foreach ($file in $Files) {
        # Set-AuthenticodeSignature signs PE files and (through Windows' MSI SIP) .msi; no Windows SDK needed.
        $sig = Set-AuthenticodeSignature -FilePath $file -Certificate $signingCert -HashAlgorithm SHA256 `
            -TimestampServer $TimestampUrl
        # A self-signed root reports UnknownError ("not trusted") but the signature is applied; check the signer.
        if (-not $sig.SignerCertificate -or $sig.SignerCertificate.Thumbprint -ne $signingCert.Thumbprint) {
            throw "Signing failed for ${file}: $($sig.StatusMessage)"
        }
        if (-not $sig.TimeStamperCertificate) { throw "No timestamp on ${file} (is $TimestampUrl reachable?)" }
    }
    Write-Host "    signed $($Files.Count) file(s) as $($signingCert.Subject)" -ForegroundColor DarkGray
}

$publish = Join-Path $PSScriptRoot "artifacts\publish"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
# The WPF tray's Release intermediates left by the previous publish break its next markup compile
# (CS2001 "*.g.cs could not be found" in the _wpftmp project). It's small: always build it fresh.
foreach ($stale in "src\Zinwe.Agent.Tray\obj\Release", "src\Zinwe.Agent.Tray\bin\Release") {
    if (Test-Path $stale) { Remove-Item $stale -Recurse -Force }
}

if (-not $SkipTests) {
    # Build the WPF tray on its own first: when Zinwe.Agent.Tray.Tests builds it from scratch as a project
    # reference, the markup-compile _wpftmp pass fails with CS2001 ("*.g.cs could not be found").
    Invoke-Step "Build tray" { dotnet build src\Zinwe.Agent.Tray -c Release }
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

if ($signingCert) {
    Write-Host "==> Sign binaries" -ForegroundColor Cyan
    # Only our own assemblies; third-party / runtime files keep their vendors' signatures.
    Invoke-Sign (Get-ChildItem $publish -Recurse -Include "Zinwe.*.exe", "Zinwe.*.dll" | ForEach-Object FullName)
}

if (-not $SkipInstaller) {
    Invoke-Step "Build MSI" {
        dotnet build installer\Zinwe.Agent.Installer.wixproj -c Release -p:ProductVersion=$Version -o artifacts\installer
    }
    $msi = Join-Path $PSScriptRoot "artifacts\installer\ZinweAgent.msi"
    # The installer's texts (Package.en-us.wxl) make WiX write the MSI into a culture folder: keep the old path.
    $cultureMsi = Join-Path $PSScriptRoot "artifacts\installer\en-US\ZinweAgent.msi"
    if (Test-Path $cultureMsi) {
        Move-Item $cultureMsi $msi -Force
        Remove-Item (Join-Path $PSScriptRoot "artifacts\installer\en-US") -Recurse -Force
    }
    if ($signingCert) {
        Write-Host "==> Sign MSI" -ForegroundColor Cyan
        Invoke-Sign @($msi)
    }
    Write-Host "MSI: $msi" -ForegroundColor Green
    if (-not $signingCert) {
        Write-Host "Unsigned build -- sign before giving it to anyone (see README, 'Code signing')." -ForegroundColor Yellow
    } elseif ($signingCert.Issuer -eq $signingCert.Subject) {
        Write-Host "Signed with a SELF-SIGNED certificate: trusted only where its .cer is imported (dev/test)." -ForegroundColor Yellow
    }
}
