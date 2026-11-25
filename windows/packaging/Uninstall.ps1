<# Entfernt Share samt „Senden an“-Eintrag. Das Zertifikat bleibt, bis es mit -RemoveCertificate entfernt wird. #>
param([switch]$RemoveCertificate)
$ErrorActionPreference = 'Stop'
Get-AppxPackage SuitIThub.Share | Remove-AppxPackage
Remove-Item (Join-Path ([Environment]::GetFolderPath('SendTo')) 'Share.lnk') -ErrorAction Ignore
if ($RemoveCertificate) {
  $thumb = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 (Join-Path $PSScriptRoot 'Share.cer')).Thumbprint
  Start-Process certutil.exe -ArgumentList '-delstore', 'TrustedPeople', $thumb -Verb RunAs -Wait -WindowStyle Hidden
}
Write-Host 'Share ist entfernt. Gerät auf dem Server entfernen: share-server devices rm <name>'
