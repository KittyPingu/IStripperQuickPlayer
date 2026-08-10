[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $MainPublishDirectory,

    [Parameter(Mandatory = $true)]
    [string] $PlayerPublishDirectory
)

$ErrorActionPreference = 'Stop'

$mainDirectory = (Resolve-Path -LiteralPath $MainPublishDirectory).Path.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
$playerDirectory = (Resolve-Path -LiteralPath $PlayerPublishDirectory).Path.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
$mainPrefix = $mainDirectory + [IO.Path]::DirectorySeparatorChar

if (-not $playerDirectory.StartsWith(
    $mainPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The player publish directory must be inside the main publish directory."
}

$manifestPath = Join-Path $playerDirectory 'hardlinks.manifest'
if (Test-Path -LiteralPath $manifestPath) {
    Remove-Item -LiteralPath $manifestPath -Force
}

$hardLinks = [Collections.Generic.List[string]]::new()
$savedBytes = [long]0
$playerFiles = @(Get-ChildItem -LiteralPath $playerDirectory -Recurse -File)

foreach ($playerFile in $playerFiles) {
    $relativePath = $playerFile.FullName.Substring(
        $playerDirectory.Length + 1)
    $mainFilePath = Join-Path $mainDirectory $relativePath
    if (-not (Test-Path -LiteralPath $mainFilePath -PathType Leaf)) {
        continue
    }

    $mainFile = Get-Item -LiteralPath $mainFilePath
    if ($mainFile.Length -ne $playerFile.Length) {
        continue
    }

    $playerHash = (Get-FileHash -Algorithm SHA256 `
        -LiteralPath $playerFile.FullName).Hash
    $mainHash = (Get-FileHash -Algorithm SHA256 `
        -LiteralPath $mainFile.FullName).Hash
    if ($playerHash -ne $mainHash) {
        continue
    }

    $hardLinks.Add($relativePath)
    $savedBytes += $playerFile.Length
    Remove-Item -LiteralPath $playerFile.FullName -Force
}

if ($hardLinks.Count -eq 0) {
    throw "No duplicate player files were found to hard-link."
}

$utf8WithoutBom = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllLines($manifestPath, $hardLinks, $utf8WithoutBom)

Write-Host ("Replaced {0} duplicate files ({1:N1} MB) with a hard-link manifest." -f `
    $hardLinks.Count, ($savedBytes / 1MB))
