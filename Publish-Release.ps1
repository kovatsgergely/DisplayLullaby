<#
.SYNOPSIS
    Publishes DisplayLullaby (Native AOT, win-x64) into .\Release and signs DisplayLullaby.exe.
#>
param(
    [int]$DisplayLullabyCommitCount = -1,
    [switch]$SkipSigning
)

$ErrorActionPreference = 'Stop'

$projectRoot = $PSScriptRoot
$projectPath = Join-Path $projectRoot 'DisplayLullaby.csproj'
$releaseDir = Join-Path $projectRoot 'Release'
$exePath = Join-Path $releaseDir 'DisplayLullaby.exe'

. (Join-Path $projectRoot 'Get-ReleaseVersion.ps1')
$releaseVersion = Get-DisplayLullabyReleaseVersion -ProjectRoot $projectRoot -CommitCount $DisplayLullabyCommitCount

New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

# Remove a previous publish so stale binaries cannot slip into the MSI (MSIs in Release are kept).
Get-ChildItem -LiteralPath $releaseDir -File |
    Where-Object { $_.Extension -in '.exe', '.dll', '.pdb', '.json', '.xml' } |
    Remove-Item -Force

Write-Host "Publishing DisplayLullaby $($releaseVersion.Version) from GitHub main commit count $($releaseVersion.CommitCount)."

dotnet publish $projectPath -c Release -r win-x64 "-p:PublishDir=$releaseDir\" "-p:DisplayLullabyCommitCount=$($releaseVersion.CommitCount)"
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

if ($SkipSigning) {
    Write-Warning "Skipping signing of $exePath (-SkipSigning)."
}
else {
    & (Join-Path $projectRoot 'Sign-Artifact.ps1') -Path $exePath -Description 'DisplayLullaby'
}
