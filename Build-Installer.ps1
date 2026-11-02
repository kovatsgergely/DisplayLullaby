<#
.SYNOPSIS
    One-command DisplayLullaby release: publish (AOT) -> sign exe -> build MSI -> sign MSI -> optional GitHub release.

.EXAMPLE
    .\Build-Installer.ps1                              # signed exe + signed MSI in .\Release
.EXAMPLE
    .\Build-Installer.ps1 -SkipSigning                 # unsigned local test build
.EXAMPLE
    .\Build-Installer.ps1 -GitHubRelease -DryRun       # show the gh command without creating anything
.EXAMPLE
    .\Build-Installer.ps1 -GitHubRelease               # create a DRAFT release v<version> with the MSI attached
#>
param(
    [switch]$SkipSigning,
    [switch]$GitHubRelease,
    [switch]$PublishRelease,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$projectRoot = $PSScriptRoot
$installerProjectPath = Join-Path $projectRoot 'Installer\DisplayLullaby.Installer.wixproj'
$productWxsPath = Join-Path $projectRoot 'Installer\Product.wxs'
$releaseDir = Join-Path $projectRoot 'Release'

if ($GitHubRelease -and $SkipSigning -and -not $DryRun) {
    throw 'Refusing to create a GitHub release with an unsigned MSI. Drop -SkipSigning (or use -DryRun).'
}

. (Join-Path $projectRoot 'Get-ReleaseVersion.ps1')
$releaseVersion = Get-DisplayLullabyReleaseVersion -ProjectRoot $projectRoot

# 1. Publish + sign DisplayLullaby.exe (signed BEFORE it is packed into the MSI).
& (Join-Path $projectRoot 'Publish-Release.ps1') -DisplayLullabyCommitCount $releaseVersion.CommitCount -SkipSigning:$SkipSigning

# 2. The publish output must match exactly what Product.wxs installs, so a new or missing native DLL fails loudly.
[xml]$wxs = Get-Content -LiteralPath $productWxsPath -Raw
$expectedFiles = @($wxs.SelectNodes("//*[local-name()='File']") | ForEach-Object { Split-Path $_.Source -Leaf }) | Sort-Object -Unique
$ignoredExtensions = '.pdb', '.msi', '.wixpdb'
$publishedFiles = @(Get-ChildItem -LiteralPath $releaseDir -File |
    Where-Object { $_.Extension -notin $ignoredExtensions } |
    ForEach-Object Name) | Sort-Object -Unique
$diff = Compare-Object -ReferenceObject $expectedFiles -DifferenceObject $publishedFiles
if ($diff) {
    $missing = @($diff | Where-Object SideIndicator -eq '<=' | ForEach-Object InputObject)
    $extra = @($diff | Where-Object SideIndicator -eq '=>' | ForEach-Object InputObject)
    throw ("Publish output in $releaseDir does not match Installer\Product.wxs.`n" +
        "  In Product.wxs but not published: $($missing -join ', ')`n" +
        "  Published but not in Product.wxs: $($extra -join ', ')`n" +
        "Update the components in Product.wxs (or remove stray files from Release).")
}
Write-Host "Publish output matches Product.wxs: $($expectedFiles -join ', ')"

# 3. Build the MSI.
$msiPath = Join-Path $releaseDir "DisplayLullaby-$($releaseVersion.Version)-x64.msi"
Remove-Item -LiteralPath $msiPath -Force -ErrorAction SilentlyContinue

# --no-incremental: always repack the freshly published (and signed) binaries.
dotnet build $installerProjectPath -c Release --no-incremental "-p:DisplayLullabyCommitCount=$($releaseVersion.CommitCount)"
if ($LASTEXITCODE -ne 0) {
    throw "MSI build failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $msiPath)) {
    throw "Expected MSI was not produced: $msiPath"
}

# 4. Sign the MSI.
if ($SkipSigning) {
    Write-Warning "Skipping signing of $msiPath (-SkipSigning)."
}
else {
    & (Join-Path $projectRoot 'Sign-Artifact.ps1') -Path $msiPath -Description 'DisplayLullaby Installer'
}

$msi = Get-Item -LiteralPath $msiPath
Write-Host ("MSI: {0} ({1:N0} bytes)" -f $msi.FullName, $msi.Length) -ForegroundColor Green

# 5. Optional GitHub release (draft unless -PublishRelease).
if ($GitHubRelease) {
    $tag = "v$($releaseVersion.Version)"
    $targetCommit = (git -C $projectRoot rev-parse HEAD).Trim()
    $mainCommit = (git -C $projectRoot rev-parse origin/main).Trim()
    if ($targetCommit -ne $mainCommit) {
        $message = "HEAD ($targetCommit) is not origin/main ($mainCommit); the version number is based on origin/main."
        if ($DryRun) { Write-Warning $message } else { throw "$message Build releases from main." }
    }

    $ghArguments = @(
        'release', 'create', $tag, $msi.FullName,
        '--repo', 'kovatsgergely/DisplayLullaby',
        '--target', $targetCommit,
        '--title', "DisplayLullaby $($releaseVersion.Version)",
        '--generate-notes'
    )
    if (-not $PublishRelease) { $ghArguments += '--draft' }

    if ($DryRun) {
        Write-Host "[DryRun] gh $($ghArguments -join ' ')" -ForegroundColor Yellow
    }
    else {
        if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI (gh) was not found on PATH.' }
        & gh @ghArguments
        if ($LASTEXITCODE -ne 0) { throw "gh release create failed with exit code $LASTEXITCODE." }
    }
}
