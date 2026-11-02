<#
.SYNOPSIS
    Signs a file (exe, dll or msi) with Azure Artifact Signing (the "Uptrack" certificate profile).

.DESCRIPTION
    Self-contained equivalent of Uptrack's Sign-UptrackMonitor.ps1. Uses signtool.exe (Windows Kits x64)
    with the Azure.CodeSigning.Dlib.dll from Microsoft Artifact Signing Client Tools. Authentication is
    the Azure CLI login stored in -AzureConfigDirectory (no secrets in this repo; run `az login` there once).
    Tokens are never printed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Path,

    [string]$AzureConfigDirectory = (Join-Path $env:LOCALAPPDATA 'SystemInfo\AzureCliBuild'),
    [string]$ExpectedAzurePrincipal = 'build@m3corp.ai',
    [ValidateSet('user', 'servicePrincipal')]
    [string]$ExpectedAzurePrincipalType = 'user',
    [string]$ExpectedAzureSubscriptionId = 'b35bbd1a-55c8-461b-86f9-03fb3051110b',
    [string]$ExpectedAzureTenantId = '2841b2c5-b831-4de6-9bad-c249fff4429b',
    [switch]$UseCurrentAzureCliProfile,
    [string]$CodeSigningEndpoint = 'https://eus.codesigning.azure.net',
    [string]$CodeSigningAccountName = 'M3AITechnologyCorp',
    [string]$CertificateProfileName = 'M3ProductivitySoftware',
    [string]$TimestampUrl = 'http://timestamp.acs.microsoft.com',
    [string]$Description = 'DisplayLullaby'
)

$ErrorActionPreference = 'Stop'

function Get-WindowsPowerShellModulePath {
    $paths = @(
        (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'WindowsPowerShell\Modules'),
        (Join-Path $env:ProgramFiles 'WindowsPowerShell\Modules'),
        (Join-Path $env:WINDIR 'system32\WindowsPowerShell\v1.0\Modules')
    )
    if (${env:ProgramFiles(x86)}) {
        $paths += Join-Path ${env:ProgramFiles(x86)} 'WindowsPowerShell\Modules'
    }
    return ($paths | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique) -join [System.IO.Path]::PathSeparator
}

function Resolve-FullPath {
    param([string]$InputPath)
    if ([System.IO.Path]::IsPathRooted($InputPath)) { return $InputPath }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $InputPath))
}

function Get-SignToolPath {
    $knownPath = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'
    if (Test-Path -LiteralPath $knownPath) { return $knownPath }

    $signTool = Get-ChildItem -Path 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($signTool) { return $signTool.FullName }
    throw 'Could not find x64 signtool.exe. Install the Windows SDK signing tools.'
}

function Test-X64PeImage {
    param([string]$ImagePath)
    try {
        $bytes = [System.IO.File]::ReadAllBytes($ImagePath)
        if ($bytes.Length -lt 64 -or [BitConverter]::ToUInt16($bytes, 0) -ne 0x5A4D) { return $false }
        $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
        if ($peOffset -lt 0 -or $peOffset + 6 -gt $bytes.Length -or [BitConverter]::ToUInt32($bytes, $peOffset) -ne 0x00004550) { return $false }
        return [BitConverter]::ToUInt16($bytes, $peOffset + 4) -eq 0x8664
    }
    catch { return $false }
}

function Get-ArtifactSigningDlibPath {
    $knownPath = Join-Path $env:LOCALAPPDATA 'Microsoft\MicrosoftArtifactSigningClientTools\Azure.CodeSigning.Dlib.dll'
    if ((Test-Path -LiteralPath $knownPath) -and (Test-X64PeImage $knownPath)) { return $knownPath }

    $searchRoots = @(
        (Join-Path $env:LOCALAPPDATA 'Microsoft\MicrosoftArtifactSigningClientTools'),
        (Join-Path $env:ProgramFiles 'Microsoft\MicrosoftArtifactSigningClientTools')
    )
    if (${env:ProgramFiles(x86)}) {
        $searchRoots += Join-Path ${env:ProgramFiles(x86)} 'Microsoft\MicrosoftArtifactSigningClientTools'
    }
    $dlib = $searchRoots |
        Where-Object { Test-Path -LiteralPath $_ } |
        ForEach-Object { Get-ChildItem -LiteralPath $_ -Recurse -Filter Azure.CodeSigning.Dlib.dll -File -ErrorAction SilentlyContinue } |
        Where-Object { Test-X64PeImage $_.FullName } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($null -ne $dlib) { return $dlib.FullName }
    throw 'Could not find an x64 Azure.CodeSigning.Dlib.dll. Install Microsoft Azure Artifact Signing Client Tools.'
}

function Invoke-CheckedCommand {
    param([string]$FilePath, [string[]]$Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($Arguments -join ' ')"
    }
}

function New-ArtifactSigningMetadata {
    param([string]$MetadataPath, [string]$Endpoint, [string]$AccountName, [string]$ProfileName)
    # Only AzureCliCredential remains, so signing uses the az login from AZURE_CONFIG_DIR.
    $json = @{
        Endpoint = $Endpoint
        CodeSigningAccountName = $AccountName
        CertificateProfileName = $ProfileName
        ExcludeCredentials = @(
            'EnvironmentCredential',
            'ManagedIdentityCredential',
            'WorkloadIdentityCredential',
            'SharedTokenCacheCredential',
            'VisualStudioCredential',
            'VisualStudioCodeCredential',
            'AzurePowerShellCredential',
            'AzureDeveloperCliCredential',
            'InteractiveBrowserCredential'
        )
    } | ConvertTo-Json
    [System.IO.File]::WriteAllText($MetadataPath, $json, [System.Text.UTF8Encoding]::new($false))
}

function Invoke-ArtifactSigning {
    param([string]$FilePath, [string]$SignTool, [string]$Dlib, [string]$MetadataPath, [string]$ContentDescription)

    $signArguments = @(
        'sign', '/v', '/debug',
        '/fd', 'SHA256',
        '/tr', $TimestampUrl,
        '/td', 'SHA256',
        '/d', $ContentDescription,
        '/dlib', $Dlib,
        '/dmdf', $MetadataPath,
        $FilePath
    )
    $maximumSigningAttempts = 3
    $signed = $false
    for ($attempt = 1; $attempt -le $maximumSigningAttempts; $attempt++) {
        $signature = Get-AuthenticodeSignature -FilePath $FilePath
        if ($signature.Status -ne 'NotSigned') {
            Invoke-CheckedCommand $SignTool @('remove', '/s', $FilePath)
        }

        & $SignTool @signArguments
        $signExitCode = $LASTEXITCODE
        if ($signExitCode -eq 0) { $signed = $true; break }

        if ($attempt -lt $maximumSigningAttempts) {
            Write-Warning "Artifact signing attempt $attempt of $maximumSigningAttempts failed with exit code $signExitCode; retrying."
            Start-Sleep -Seconds (2 * $attempt)
        }
    }
    if (-not $signed) { throw "Artifact signing failed after $maximumSigningAttempts attempts: $FilePath" }

    Invoke-CheckedCommand $SignTool @('verify', '/pa', '/v', $FilePath)
}

$targetPath = Resolve-FullPath $Path
if (-not (Test-Path -LiteralPath $targetPath)) { throw "File to sign not found: $targetPath" }
if ([string]::IsNullOrWhiteSpace($Description)) { throw 'Signed-content description must not be empty.' }

$previousAzureConfigDirectory = $env:AZURE_CONFIG_DIR
$previousPSModulePath = $env:PSModulePath
$metadataPath = Join-Path $env:TEMP "displaylullaby-artifact-signing-$([System.Guid]::NewGuid().ToString('N')).metadata.json"

$effectiveExpectedPrincipal = if ($env:DISPLAYLULLABY_SIGNING_EXPECTED_AZURE_PRINCIPAL) { $env:DISPLAYLULLABY_SIGNING_EXPECTED_AZURE_PRINCIPAL } else { $ExpectedAzurePrincipal }
$effectiveExpectedPrincipalType = if ($env:DISPLAYLULLABY_SIGNING_EXPECTED_AZURE_PRINCIPAL_TYPE) { $env:DISPLAYLULLABY_SIGNING_EXPECTED_AZURE_PRINCIPAL_TYPE } else { $ExpectedAzurePrincipalType }
$effectiveUseCurrentAzureCliProfile = $UseCurrentAzureCliProfile -or $env:DISPLAYLULLABY_SIGNING_USE_CURRENT_AZURE_CLI_PROFILE -eq 'true'

if ($effectiveExpectedPrincipalType -notin @('user', 'servicePrincipal')) {
    throw "Expected Azure principal type must be 'user' or 'servicePrincipal', got '$effectiveExpectedPrincipalType'."
}

try {
    $env:PSModulePath = Get-WindowsPowerShellModulePath
    if (-not $effectiveUseCurrentAzureCliProfile) {
        $env:AZURE_CONFIG_DIR = $AzureConfigDirectory
    }

    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        throw 'Azure CLI (az) was not found on PATH.'
    }
    $accountJson = & az account show --query '{principal:user.name, principalType:user.type, subscriptionId:id, tenantId:tenantId}' --output json
    if ($LASTEXITCODE -ne 0) {
        throw "Could not read the active Azure CLI account. Run 'az login' with AZURE_CONFIG_DIR=$env:AZURE_CONFIG_DIR as $effectiveExpectedPrincipal."
    }
    $account = $accountJson | ConvertFrom-Json

    if ($account.principal -ine $effectiveExpectedPrincipal) { throw "Expected Azure CLI principal '$effectiveExpectedPrincipal', got '$($account.principal)'." }
    if ($account.principalType -ine $effectiveExpectedPrincipalType) { throw "Expected Azure CLI principal type '$effectiveExpectedPrincipalType', got '$($account.principalType)'." }
    if ($account.subscriptionId -ine $ExpectedAzureSubscriptionId) { throw "Expected Azure subscription '$ExpectedAzureSubscriptionId', got '$($account.subscriptionId)'." }
    if ($account.tenantId -ine $ExpectedAzureTenantId) { throw "Expected Azure tenant '$ExpectedAzureTenantId', got '$($account.tenantId)'." }

    New-ArtifactSigningMetadata $metadataPath $CodeSigningEndpoint $CodeSigningAccountName $CertificateProfileName
    $signTool = Get-SignToolPath
    $dlib = Get-ArtifactSigningDlibPath

    Write-Host "Signing $targetPath as $($account.principal) ($($account.principalType))" -ForegroundColor Cyan
    Invoke-ArtifactSigning $targetPath $signTool $dlib $metadataPath $Description
}
finally {
    $env:AZURE_CONFIG_DIR = $previousAzureConfigDirectory
    $env:PSModulePath = $previousPSModulePath
    Remove-Item -LiteralPath $metadataPath -Force -ErrorAction SilentlyContinue
}
