[CmdletBinding()]
param(
    [string]$RuntimeRoot = (Join-Path $env:LOCALAPPDATA 'IStripperQuickPlayer\sam2matting-sam3-worker\v1'),
    [string]$PythonLauncher = 'py',
    [switch]$Repair
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$setupLock = [System.Threading.Semaphore]::new(
    1, 1, 'Local\IStripperQuickPlayer.Sam2MattingSetup')
if (-not $setupLock.WaitOne(0)) {
    throw 'SAM2Matting setup is already running.'
}

$sourceRevision = '73dd721d77b56749248aefe5e8824d7f61b9d13c'
$checkpointRevision = '4315db9c60d27fde396b09765748a0ca6c97bed5'
$runtime = [System.IO.Path]::GetFullPath($RuntimeRoot)
$venv = Join-Path $runtime 'venv'
$sourceRoot = Join-Path $runtime 'source\SAM2Matting'
$checkpoints = Join-Path $runtime 'checkpoints'
$cache = Join-Path $runtime 'cache'
$requirementsLock = Join-Path $PSScriptRoot 'sam2matting-requirements.lock'

function Get-Sha256([string]$path) {
    $stream = [System.IO.File]::Open($path, [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
        $algorithm = [System.Security.Cryptography.SHA256]::Create()
        try {
            return ([System.BitConverter]::ToString(
                $algorithm.ComputeHash($stream))).Replace('-', '')
        } finally { $algorithm.Dispose() }
    } finally { $stream.Dispose() }
}

function Get-Python310([string]$launcher) {
    if ([System.IO.Path]::GetFileNameWithoutExtension($launcher) -ieq 'py') {
        $savedErrorPreference = $ErrorActionPreference
        try {
            # py.exe writes its normal interpreter inventory to stderr. Windows
            # PowerShell otherwise promotes that output to NativeCommandError
            # when the setup script uses ErrorActionPreference=Stop.
            $ErrorActionPreference = 'SilentlyContinue'
            $lines = & $launcher -0p 2>$null
            $launcherExitCode = $LASTEXITCODE
        } finally { $ErrorActionPreference = $savedErrorPreference }
        if ($launcherExitCode -ne 0) { return $null }
        foreach ($line in $lines) {
            if ($line -match '^\s*-3\.10(?:-\d+)?\s+(.+python\.exe)\s*$') {
                return $Matches[1].Trim()
            }
        }
        return $null
    }
    $savedErrorPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'SilentlyContinue'
        $candidate = & $launcher -c `
            'import sys; raise SystemExit(1) if sys.version_info[:2] != (3, 10) else print(sys.executable)' `
            2>$null
        $launcherExitCode = $LASTEXITCODE
    } finally { $ErrorActionPreference = $savedErrorPreference }
    if ($launcherExitCode -eq 0) { return $candidate }
    return $null
}

function Get-VerifiedDownload(
    [string]$Uri, [string]$Path, [long]$Size, [string]$Sha256) {
    if ((Test-Path -LiteralPath $Path)) {
        $file = Get-Item -LiteralPath $Path
        if ($file.Length -eq $Size -and
            (Get-Sha256 $Path) -eq $Sha256) {
            Write-Host "Using verified $($file.Name)."
            return
        }
    }
    $temporary = "$Path.download"
    try {
        $received = if (Test-Path -LiteralPath $temporary) {
            (Get-Item -LiteralPath $temporary).Length
        } else { 0L }
        if ($received -gt $Size) {
            Remove-Item -LiteralPath $temporary -Force
            $received = 0L
        }
        $verb = if ($received -gt 0) { 'Resuming' } else { 'Downloading' }
        Write-Host "$verb $(Split-Path $Path -Leaf)..."
        $client = [System.Net.Http.HttpClient]::new()
        $request = [System.Net.Http.HttpRequestMessage]::new(
            [System.Net.Http.HttpMethod]::Get, $Uri)
        if ($received -gt 0) {
            $request.Headers.Range = [System.Net.Http.Headers.RangeHeaderValue]::new(
                $received, $null)
        }
        $response = $client.SendAsync($request,
            [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        if ($received -gt 0 -and
            $response.StatusCode -ne [System.Net.HttpStatusCode]::PartialContent) {
            $response.Dispose()
            $request.Dispose()
            Remove-Item -LiteralPath $temporary -Force
            $received = 0L
            $request = [System.Net.Http.HttpRequestMessage]::new(
                [System.Net.Http.HttpMethod]::Get, $Uri)
            $response = $client.SendAsync($request,
                [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        }
        $response.EnsureSuccessStatusCode() | Out-Null
        $input = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $fileMode = if ($received -gt 0) {
            [System.IO.FileMode]::Append
        } else { [System.IO.FileMode]::Create }
        $output = [System.IO.File]::Open($temporary, $fileMode,
            [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try {
            $buffer = New-Object byte[] (4MB)
            $next = [DateTime]::UtcNow
            while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $output.Write($buffer, 0, $read)
                $received += $read
                if ([DateTime]::UtcNow -ge $next) {
                    Write-Host ('##quickplayer-progress##  {0:N2}/{1:N2} GiB' -f `
                        ($received / 1GB), ($Size / 1GB))
                    $next = [DateTime]::UtcNow.AddSeconds(2)
                }
            }
        } finally {
            $output.Dispose(); $input.Dispose(); $response.Dispose()
            $request.Dispose(); $client.Dispose()
        }
        if ((Get-Item -LiteralPath $temporary).Length -ne $Size) {
            throw "File-size mismatch for $(Split-Path $Path -Leaf)."
        }
        $actual = Get-Sha256 $temporary
        if ($actual -ne $Sha256) {
            throw "SHA-256 mismatch for $(Split-Path $Path -Leaf): $actual"
        }
        Move-Item -LiteralPath $temporary -Destination $Path -Force
    } catch {
        if ((Test-Path -LiteralPath $temporary) -and
            (Get-Item -LiteralPath $temporary).Length -ge $Size) {
            Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        }
        throw
    }
}

try {
    Write-Host 'Fudan SAM2Matting is provided for non-commercial use under its upstream licence.'
    Write-Host 'QuickPlayer downloads pinned source and public weights; neither is bundled with the app.'
    $basePython = Get-Python310 $PythonLauncher
    if (-not $basePython) {
        $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
        if (-not $winget) {
            throw 'CPython 3.10 is required. Install it from python.org and retry.'
        }
        Write-Host 'Installing CPython 3.10 for the current user...'
        & $winget.Source install --id Python.Python.3.10 --exact --scope user `
            --accept-package-agreements --accept-source-agreements
        if ($LASTEXITCODE -ne 0) {
            throw "Python 3.10 installation failed with exit code $LASTEXITCODE."
        }
        $basePython = Get-Python310 $PythonLauncher
    }
    if (-not $basePython) { throw 'A CPython 3.10 interpreter was not found.' }
    if (-not (Test-Path -LiteralPath $requirementsLock)) {
        throw 'The hash-locked SAM2Matting requirements file is missing.'
    }

    New-Item -ItemType Directory -Force -Path $runtime,$checkpoints,$cache,
        (Split-Path $sourceRoot -Parent) | Out-Null
    if ($Repair -and (Test-Path -LiteralPath $venv)) {
        Remove-Item -LiteralPath $venv -Recurse -Force
    }
    if (-not (Test-Path -LiteralPath (Join-Path $venv 'Scripts\python.exe'))) {
        & $basePython -m venv $venv
    }
    $python = Join-Path $venv 'Scripts\python.exe'
    & $python -m pip install --disable-pip-version-check 'pip==25.2'
    if ($LASTEXITCODE -ne 0) { throw 'Pinned pip installation failed.' }
    & $python -m pip install --disable-pip-version-check --require-hashes `
        --requirement $requirementsLock
    if ($LASTEXITCODE -ne 0) { throw 'Pinned Python dependency installation failed.' }

    if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot '.git'))) {
        git clone 'https://github.com/FudanCVL/SAM2Matting.git' $sourceRoot
    }
    git -C $sourceRoot fetch --depth 1 origin $sourceRevision
    git -C $sourceRoot checkout --detach $sourceRevision
    if ((git -C $sourceRoot rev-parse HEAD).Trim() -ne $sourceRevision) {
        throw 'Fudan source revision verification failed.'
    }

    $files = @(
        @{ Name='SAM2Matting-SAM2.1Tiny.pt'; Size=215569778L; Hash='5B9321E3B51BC20F5B84C208746CC083DD3053DD701590F2E88DC8640AFCC39D' },
        @{ Name='SAM2Matting-SAM2.1Base+.pt'; Size=383180506L; Hash='1F0EB2EDA3E8BC9101EAFC0B30B8B8FCAE1FF83D8FD3ADC18E2F3B410FDAAE60' },
        @{ Name='SAM2Matting-SAM3.pt'; Size=3509720141L; Hash='7102D695BE6070B39ACD67464F93207DF725514A688B545ED1267D913D3B9C7D' }
    )
    foreach ($file in $files) {
        $uri = "https://huggingface.co/FudanCVL/SAM2Matting/resolve/$checkpointRevision/checkpoints/$($file.Name)?download=true"
        Get-VerifiedDownload $uri (Join-Path $checkpoints $file.Name) `
            $file.Size $file.Hash
    }

    $environment = [ordered]@{
        environmentSpecVersion = 'sam2matting-v1'
        sourceRevision = $sourceRevision
        checkpointRevision = $checkpointRevision
        pythonVersion = '3.10'
        torchVersion = '2.8.0'
        torchvisionVersion = '0.23.0'
        cudaWheel = 'cu128'
        attentionPolicy = 'pytorch-sdpa'
        precisionPolicy = 'eager-bf16'
        encoderPolicy = 'quickplayer-h264-aac'
        alphaEncodingPolicy = 'ffv1-gray16le-linear'
        requirementsLockSha256 = (Get-Sha256 $requirementsLock).ToLowerInvariant()
        checkpoints = $files
    }
    $environment | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath `
        (Join-Path $runtime 'environment.json') -Encoding utf8

    $worker = Join-Path $PSScriptRoot 'sam2matting_worker.py'
    try {
        & $python $worker --validate --runtime $runtime --full-hash
        if ($LASTEXITCODE -ne 0) {
            throw 'SAM2Matting environment validation failed.'
        }
    } catch {
        Remove-Item -LiteralPath (Join-Path $runtime 'environment.json') `
            -Force -ErrorAction SilentlyContinue
        throw
    }
    Write-Host "SAM2Matting setup complete: $runtime"
} finally {
    $setupLock.Release() | Out-Null
    $setupLock.Dispose()
}
