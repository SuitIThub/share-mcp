<#
  Installiert oder aktualisiert Share. Aufruf (Rechtsklick „Mit PowerShell ausführen“ oder):  powershell -ExecutionPolicy Bypass -File Install.ps1
  Beim ersten Mal fragt Windows einmal nach Administratorrechten, um dem Zertifikat zu vertrauen.
#>
$ErrorActionPreference = 'Stop'
$cer = Join-Path $PSScriptRoot 'Share.cer'
$thumb = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $cer).Thumbprint
if (-not (Test-Path "Cert:\LocalMachine\TrustedPeople\$thumb")) {
  Write-Host 'Das Zertifikat wird einmalig als vertrauenswürdig eingetragen ...'
  $p = Start-Process certutil.exe -ArgumentList '-addstore', 'TrustedPeople', "`"$cer`"" -Verb RunAs -Wait -PassThru -WindowStyle Hidden
  if ($p.ExitCode) { throw 'Das Zertifikat konnte nicht eingetragen werden.' }
}
Add-AppxPackage -Path (Join-Path $PSScriptRoot 'Share.msix') -ForceApplicationShutdown

# „Senden an“ im Explorer
$alias = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\share-senden.exe'
$link = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path ([Environment]::GetFolderPath('SendTo')) 'Share.lnk'))
$link.TargetPath = $alias
$link.Arguments = '--send'
$link.Description = 'An eigene Geräte senden'
$link.Save()
$app = Get-StartApps | Where-Object Name -eq 'Share' | Select-Object -First 1
if ($app) { Start-Process "shell:AppsFolder\$($app.AppID)" }
Write-Host 'Share ist installiert.'
