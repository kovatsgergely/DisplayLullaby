param(
    [string]$CertificateSubject = 'CN=DisplayLullaby'
)

$ErrorActionPreference = 'Stop'

$projectRoot = $PSScriptRoot
$versionPropsPath = Join-Path $projectRoot 'Version.props'
$installerProjectPath = Join-Path $projectRoot 'Installer\DisplayLullaby.Installer.wixproj'
$releaseDir = Join-Path $projectRoot 'Release'

[xml]$versionProps = Get-Content -Path $versionPropsPath
$version = $versionProps.Project.PropertyGroup.DisplayLullabyVersion
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "DisplayLullabyVersion was not found in $versionPropsPath."
}

& (Join-Path $projectRoot 'Publish-Release.ps1') -CertificateSubject $CertificateSubject
if ($LASTEXITCODE -ne 0) {
    throw "Publish-Release.ps1 failed with exit code $LASTEXITCODE."
}

dotnet build $installerProjectPath -c Release
if ($LASTEXITCODE -ne 0) {
    throw "MSI build failed with exit code $LASTEXITCODE."
}

$msiPath = Join-Path $releaseDir "DisplayLullaby-$version-x64.msi"
if (-not (Test-Path -LiteralPath $msiPath)) {
    throw "Expected MSI was not produced: $msiPath"
}

$cert = Get-ChildItem -Path Cert:\CurrentUser\My | Where-Object {
    $_.Subject -eq $CertificateSubject -and
    $_.HasPrivateKey -and
    ($_.EnhancedKeyUsageList | Where-Object { $_.FriendlyName -eq 'Code Signing' })
} | Sort-Object NotAfter -Descending | Select-Object -First 1

if (-not $cert) {
    throw "No code-signing certificate with subject '$CertificateSubject' was found in Cert:\CurrentUser\My."
}

$signature = Set-AuthenticodeSignature -FilePath $msiPath -Certificate $cert -HashAlgorithm SHA256
$signature | Format-List Status,StatusMessage,SignerCertificate,Path
