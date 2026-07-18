param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryRoot
)

$tag = & git -C $RepositoryRoot describe --tags --abbrev=0 --match 'v[0-9]*'
if ($LASTEXITCODE -ne 0 -or $tag -notmatch '^v(\d+)\.(\d+)(?:\.(\d+))?') {
    '0.0.0-dev'
    exit
}

$series = "$($Matches[1]).$($Matches[2])"
$releasePatch = if ($Matches[3]) { [int]$Matches[3] } else { 0 }
$counterFolder = Join-Path $env:LOCALAPPDATA 'IStripperQuickPlayer\build-counters'
$counterFile = Join-Path $counterFolder "$series.txt"
$build = $releasePatch
if (Test-Path -LiteralPath $counterFile) {
    [int]::TryParse(
        (Get-Content -LiteralPath $counterFile -Raw).Trim(),
        [ref]$build) | Out-Null
    $build = [Math]::Max($build, $releasePatch)
}

$build++
[IO.Directory]::CreateDirectory($counterFolder) | Out-Null
[IO.File]::WriteAllText($counterFile, $build.ToString())
"$series.$build-dev"
