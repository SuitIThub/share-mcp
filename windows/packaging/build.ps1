<#
  Baut das signierte Installationspaket nach windows/dist.
  Aufruf:  pwsh packaging/build.ps1 [-Version 1.2.0.0] [-Dotnet C:\Pfad\dotnet.exe] [-Loose]
  -Loose: nur den Paketordner vorbereiten (windows/dist/loose), nicht packen und signieren.
#>
param([string]$Version = '1.0.0.0', [string]$Dotnet = 'dotnet', [switch]$Loose, [string]$Name = 'SuitIThub.Share', [string]$DisplayName = 'Share', [string]$Alias = 'share-senden.exe')
$ErrorActionPreference = 'Stop'
$publisher = "CN=Share (privat)"
$windows = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $windows 'dist'
$layout = Join-Path $dist 'loose'
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory $layout | Out-Null

# makeappx, makepri und signtool kommen als NuGet-Paket, kein Windows-SDK nötig
$tools = Join-Path $PSScriptRoot 'tools.csproj'
& $Dotnet restore $tools; if ($LASTEXITCODE) { throw 'restore' }
$packages = (& $Dotnet nuget locals global-packages --list) -replace '^[^:]+:\s*', ''
$bin = Get-ChildItem (Join-Path $packages 'microsoft.windows.sdk.buildtools/10.0.28000.2957/bin') -Directory | Select-Object -First 1
$bin = Join-Path $bin.FullName 'x64'

& $Dotnet publish (Join-Path $windows 'src/Share.App') -c Release -r win-x64 --self-contained true -p:Version=$($Version -replace '\.\d+$', '') -o $layout
if ($LASTEXITCODE) { throw 'publish' }
Remove-Item (Join-Path $layout '*.pdb') -ErrorAction Ignore
Copy-Item (Join-Path $PSScriptRoot 'assets') $layout -Recurse
[xml]$m = Get-Content (Join-Path $PSScriptRoot 'AppxManifest.xml') -Encoding UTF8
$m.Package.Identity.Version = $Version
$m.Package.Identity.Name = $Name
$m.Package.Properties.DisplayName = $DisplayName
$m.Package.Applications.Application.VisualElements.DisplayName = $DisplayName
$m.Package.Applications.Application.Extensions.ChildNodes | Where-Object { $_.Category -eq 'windows.appExecutionAlias' } | ForEach-Object { $_.AppExecutionAlias.ExecutionAlias.Alias = $Alias }
$m.Save((Join-Path $layout 'AppxManifest.xml'))

# Verzeichnis der Symbolgrößen
$pri = Join-Path $dist 'priconfig.xml'
& (Join-Path $bin 'makepri.exe') createconfig /cf $pri /dq de-DE /o | Out-Null; if ($LASTEXITCODE) { throw 'makepri createconfig' }
& (Join-Path $bin 'makepri.exe') new /pr $layout /cf $pri /mn (Join-Path $layout 'AppxManifest.xml') /of (Join-Path $layout 'resources.pri') /o | Out-Null; if ($LASTEXITCODE) { throw 'makepri new' }
Remove-Item $pri
if ($Loose) { Write-Host "Paketordner: $layout"; return }

$msix = Join-Path $dist 'Share.msix'
& (Join-Path $bin 'makeappx.exe') pack /d $layout /p $msix /o | Out-Null; if ($LASTEXITCODE) { throw 'makeappx' }

# eigenes Zertifikat; bitte sichern (siehe docs/windows.md), sonst sind keine Updates möglich
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } | Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
  $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature -FriendlyName 'Share Paketsignatur' -CertStoreLocation Cert:\CurrentUser\My `
    -NotAfter (Get-Date).AddYears(15) -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
  Write-Host "Neues Signaturzertifikat erzeugt: $($cert.Thumbprint)"
}
& (Join-Path $bin 'signtool.exe') sign /fd SHA256 /sha1 $cert.Thumbprint $msix | Out-Null; if ($LASTEXITCODE) { throw 'signtool' }
Export-Certificate -Cert $cert -FilePath (Join-Path $dist 'Share.cer') | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'Install.ps1'), (Join-Path $PSScriptRoot 'Uninstall.ps1') $dist -Force
Write-Host "Fertig: $msix"
