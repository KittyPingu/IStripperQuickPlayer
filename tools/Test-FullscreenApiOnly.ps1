<#
Close QuickPlayer, including its tray instance, before running this script.
For a cold test, also exit and reopen iStripper first. Leave its interface open.
The script leaves QuickPlayer in API-only mode and leaves a successful scene running.

Example:
  .\Test-FullscreenApiOnly.ps1 -SceneId 'Ball Tracking/Ball Tracking.scn'
Use -QuickPlayerExe to specify the Release executable on another computer.
#>
[CmdletBinding()]
param(
    [string]$SceneId = 'Ball Tracking/Ball Tracking.scn',
    [string]$QuickPlayerExe = "$env:USERPROFILE\source\repos\KittyPingu\IStripperQuickPlayer\IstripperQuickPlayer\bin\x64\Release\net10.0-windows\IstripperQuickPlayer.exe",
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

function Test-LookupTimeout([int]$StatusCode, [string]$Message) {
    return $StatusCode -eq 409 -and $Message -match 'controller lookup timed out' -and
        $Message -match '0x800705B4'
}

if ($SelfTest) {
    if (-not (Test-LookupTimeout 409 'The fullscreen scene controller lookup timed out (0x800705B4).')) {
        throw 'Native lookup timeout was not recognized.'
    }
    foreach ($sample in @(
        @(409, 'Transport timeout (0x800705B4).'),
        @(500, 'The fullscreen scene controller lookup timed out (0x800705B4).'),
        @(409, 'The fullscreen scene controller was not found (0x80070490).'),
        @(202, 'accepted')
    )) {
        if (Test-LookupTimeout $sample[0] $sample[1]) { throw 'Unsafe retry classification.' }
    }
    Write-Output 'PASS: only explicit native controller-lookup timeouts are retriable.'
    return
}

if (-not (Test-Path -LiteralPath $QuickPlayerExe -PathType Leaf)) {
    throw 'QuickPlayer executable missing. Supply its full path with -QuickPlayerExe.'
}
if (Get-Process IstripperQuickPlayer -ErrorAction SilentlyContinue) {
    throw 'Close QuickPlayer, including its tray instance, before testing API-only startup.'
}
$hostApp = @(Get-Process vghd -ErrorAction SilentlyContinue)
if ($hostApp.Count -ne 1) { throw 'Start one iStripper instance first.' }

$startedUtc = [DateTime]::UtcNow
$app = Start-Process -FilePath $QuickPlayerExe -ArgumentList '--api-only' `
    -WorkingDirectory (Split-Path -Parent $QuickPlayerExe) -WindowStyle Hidden -PassThru
$baseUrl = 'http://127.0.0.1:17871/api/v1'
$dataRoot = Join-Path $env:LOCALAPPDATA 'IStripperQuickPlayer'
$tokenPath = Join-Path $dataRoot 'api-token.txt'
$headers = @{}
$ready = $false
$startup = [Diagnostics.Stopwatch]::StartNew()
while ($startup.Elapsed.TotalSeconds -lt 30) {
    $app.Refresh()
    if ($app.HasExited) { throw 'QuickPlayer exited before the API was ready.' }
    try {
        $headers.Authorization = 'Bearer ' + (Get-Content -LiteralPath $tokenPath -Raw).Trim()
        $status = Invoke-RestMethod "$baseUrl/status" -Headers $headers -TimeoutSec 2
        if ($status.player.playbackControlsAvailable) { $ready = $true; break }
    } catch { }
    Start-Sleep -Milliseconds 250
}
if (-not $ready) { throw 'API/bridge not ready after 30 seconds. Check the QuickPlayer tray icon.' }

$scenes = (Invoke-RestMethod "$baseUrl/fullscreen/scenes" -Headers $headers -TimeoutSec 5).scenes
if (-not ($scenes | Where-Object { $_.id -eq $SceneId })) {
    $scenes | Select-Object id, name | Format-Table -AutoSize | Out-Host
    throw 'Scene not installed. Run again with a sceneId from the list above.'
}

$attempts = @()
$body = @{ sceneId = $SceneId } | ConvertTo-Json
for ($number = 1; $number -le 3; $number++) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $code = 0
    $message = ''
    try {
        $reply = Invoke-RestMethod "$baseUrl/fullscreen/scenes/start" -Method Post `
            -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 25
        $code = 202
        $message = $reply | ConvertTo-Json -Compress
    } catch {
        if ($null -ne $_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
        $message = $_.ErrorDetails.Message
        if ([string]::IsNullOrWhiteSpace($message)) { $message = $_.Exception.Message }
    }
    $attempts += [pscustomobject]@{
        request = $number; httpStatus = $code; milliseconds = $timer.ElapsedMilliseconds; response = $message
    }
    Write-Host "Request $number`: HTTP $code, $($timer.ElapsedMilliseconds) ms"
    if (-not (Test-LookupTimeout $code $message)) { break }
    # The native error guarantees no dispatch; never replay an uncertain transport failure.
    if ($number -lt 3) { Start-Sleep -Milliseconds 250 }
}

Start-Sleep -Milliseconds 750
$fullscreenActive = $null
$statusError = $null
try {
    $fullscreenActive = (Invoke-RestMethod "$baseUrl/fullscreen" -Headers $headers -TimeoutSec 5).active
} catch { $statusError = $_.Exception.Message }
$lookups = @()
$logPath = Join-Path $dataRoot 'desktop-playback.jsonl'
if (Test-Path -LiteralPath $logPath) {
    foreach ($line in (Get-Content -LiteralPath $logPath -Tail 3000)) {
        try { $entry = $line | ConvertFrom-Json } catch { continue }
        if ($entry.process -eq $app.Id -and [DateTime]$entry.utc -ge $startedUtc -and
            $entry.action -in @('desktop-native-scene-controller-start', 'desktop-native-scene-controller-result')) {
            $lookups += [pscustomobject]@{ utc = $entry.utc; event = $entry.action; details = $entry.details }
        }
    }
}
$report = [ordered]@{
    mode = 'api-only'; startedUtc = $startedUtc.ToString('o'); quickPlayerPid = $app.Id
    quickPlayerExe = (Resolve-Path -LiteralPath $QuickPlayerExe).Path; istripperPid = $hostApp[0].Id
    sceneId = $SceneId; requests = $attempts; fullscreenActive = $fullscreenActive
    statusError = $statusError; lookups = $lookups
}
$reportPath = Join-Path (Get-Location).Path ("fullscreen-api-only-{0}.json" -f [Guid]::NewGuid().ToString('N'))
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding UTF8
Write-Host "Fullscreen active: $fullscreenActive (HTTP 202 alone only confirms dispatch)."
Write-Host "Report: $reportPath"
Write-Host 'The report excludes the API token. QuickPlayer remains in API-only mode.'
