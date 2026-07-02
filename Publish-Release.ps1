param(
    [string]$CertificateSubject = 'CN=DisplayLullaby'
)

$ErrorActionPreference = 'Stop'

$projectRoot = $PSScriptRoot
$projectPath = Join-Path $projectRoot 'DisplayLullaby.csproj'
$releaseDir = Join-Path $projectRoot 'Release'
$exePath = Join-Path $releaseDir 'DisplayLullaby.exe'

New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

dotnet publish $projectPath -c Release -r win-x64 "-p:PublishDir=$releaseDir\"
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$cert = Get-ChildItem -Path Cert:\CurrentUser\My | Where-Object {
    $_.Subject -eq $CertificateSubject -and
    $_.HasPrivateKey -and
    ($_.EnhancedKeyUsageList | Where-Object { $_.FriendlyName -eq 'Code Signing' })
} | Sort-Object NotAfter -Descending | Select-Object -First 1

if (-not $cert) {
    throw "No code-signing certificate with subject '$CertificateSubject' was found in Cert:\CurrentUser\My."
}

$signature = Set-AuthenticodeSignature -FilePath $exePath -Certificate $cert -HashAlgorithm SHA256
$signature | Format-List Status,StatusMessage,SignerCertificate,Path
