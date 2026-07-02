function Get-DisplayLullabyReleaseVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectRoot,

        [int]$CommitCount = -1
    )

    $versionPropsPath = Join-Path $ProjectRoot 'Version.props'
    [xml]$versionProps = Get-Content -Path $versionPropsPath
    $prefix = $versionProps.Project.PropertyGroup.DisplayLullabyVersionPrefix
    if ([string]::IsNullOrWhiteSpace($prefix)) {
        throw "DisplayLullabyVersionPrefix was not found in $versionPropsPath."
    }

    if ($CommitCount -lt 0) {
        git -C $ProjectRoot fetch --quiet origin main:refs/remotes/origin/main
        if ($LASTEXITCODE -ne 0) {
            throw "Could not fetch origin/main to calculate the GitHub commit count."
        }

        $commitCountText = git -C $ProjectRoot rev-list --count origin/main
        if ($LASTEXITCODE -ne 0) {
            throw "Could not calculate the origin/main commit count."
        }

        $CommitCount = [int]($commitCountText | Select-Object -First 1)
    }

    [pscustomobject]@{
        Prefix = $prefix
        CommitCount = $CommitCount
        Version = "$prefix.$CommitCount"
    }
}
