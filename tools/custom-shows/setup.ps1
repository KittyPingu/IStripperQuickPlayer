[CmdletBinding()]
param(
    [string]$RuntimeRoot = (Join-Path $env:LOCALAPPDATA 'IStripperQuickPlayer\rvm-runtime'),
    [string]$PythonLauncher = 'py',
    [switch]$InstallTransNetV2,
    [switch]$InstallOmniShotCut,
    [switch]$InstallMatAnyone2,
    [switch]$InstallVideoMaMa,
    [switch]$InstallViTMatte,
    [switch]$InstallProPainter
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$setupLock = [System.Threading.Semaphore]::new(1, 1, 'Local\IStripperQuickPlayer.CustomShowSetup')
if (-not $setupLock.WaitOne(0)) { throw 'Custom-show processing setup is already running.' }
$commit = '53d74c6826735f01f4406b5ca9075eee27bec094'
$runtime = [System.IO.Path]::GetFullPath($RuntimeRoot)
$venv = Join-Path $runtime 'venv'
$rvm = Join-Path $runtime 'rvm'
$checkpoints = Join-Path $runtime 'checkpoints'
if ($PythonLauncher -eq 'py' -and (Test-Path (Join-Path $env:WINDIR 'py.exe'))) {
    $PythonLauncher = Join-Path $env:WINDIR 'py.exe'
}
Write-Host "Using Python launcher: $PythonLauncher"
function Get-VerifiedDownload([string]$uri, [string]$path, [string]$expectedHash) {
    if ((Test-Path -LiteralPath $path) -and
        (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash -eq $expectedHash) {
        Write-Host "Using existing $(Split-Path $path -Leaf) (verified)."
        return
    }
    $temporary = "$path.download"
    try {
        Write-Host "Downloading $(Split-Path $path -Leaf)..."
        $client = [System.Net.Http.HttpClient]::new()
        $response = $client.GetAsync($uri,
            [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        $response.EnsureSuccessStatusCode() | Out-Null
        $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $file = [System.IO.File]::Open($temporary, [System.IO.FileMode]::Create,
            [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try {
            $buffer = New-Object byte[] (1024 * 1024)
            $received = [int64]0
            $total = $response.Content.Headers.ContentLength
            $nextReport = [DateTime]::UtcNow
            while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $file.Write($buffer, 0, $read)
                $received += $read
                if ([DateTime]::UtcNow -ge $nextReport) {
                    if ($total) {
                        Write-Host ("  {0:N1}% ({1:N2}/{2:N2} GB)" -f
                            (100 * $received / $total), ($received / 1GB), ($total / 1GB))
                    } else { Write-Host ("  {0:N2} GB" -f ($received / 1GB)) }
                    $nextReport = [DateTime]::UtcNow.AddSeconds(2)
                }
            }
        } finally {
            $file.Dispose(); $stream.Dispose(); $response.Dispose(); $client.Dispose()
        }
        $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $temporary).Hash
        if ($actual -ne $expectedHash) { throw "SHA-256 mismatch for $(Split-Path $path -Leaf): $actual" }
        Move-Item -LiteralPath $temporary -Destination $path -Force
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}
function Find-CompatiblePython([string]$launcher) {
    $savedErrorPreference = $ErrorActionPreference
    $ErrorActionPreference = 'SilentlyContinue'
    $installedPythons = & $launcher -0p 2>$null
    $ErrorActionPreference = $savedErrorPreference
    $candidates = @($installedPythons | ForEach-Object {
        if ($_ -match '^\s*-(3\.(?:11|12|13|14))(?:-\d+)?\s+(.+python\.exe)\s*$') {
            [pscustomobject]@{ Version = [version]$Matches[1]; Path = $Matches[2].Trim() }
        }
    })
    return ($candidates | Sort-Object Version -Descending | Select-Object -First 1).Path
}
Write-Host 'Checking for a supported Python (3.11-3.14)...'
if ([System.IO.Path]::GetFileNameWithoutExtension($PythonLauncher) -ieq 'py') {
    $basePython = Find-CompatiblePython $PythonLauncher
} else {
    $basePython = & $PythonLauncher -c 'import sys; raise SystemExit(1) if not ((3, 11) <= sys.version_info[:2] < (3, 15)) else print(sys.executable)'
    if ($LASTEXITCODE -ne 0) { $basePython = $null }
}
if (-not $basePython) {
    $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
    if (-not $winget) { throw 'Python 3.11-3.14 is required. Install Python 3.12 from python.org.' }
    Write-Host 'No supported Python was found; installing Python 3.12 for the current user...'
    & $winget.Source install --id Python.Python.3.12 --exact --scope user --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) { throw "Python 3.12 installation failed with exit code $LASTEXITCODE." }
    $basePython = Find-CompatiblePython $PythonLauncher
}
if (-not $basePython) { throw 'A supported Python installation was not found after setup.' }
Write-Host "Using Python: $basePython"
Write-Host 'Creating the isolated Python environment...'
New-Item -ItemType Directory -Force -Path $runtime,$checkpoints | Out-Null
& $basePython -m venv $venv
$python = Join-Path $venv 'Scripts\python.exe'
Write-Host 'Installing Python packages...'
& $python -m pip install --upgrade pip
& $python -m pip install torch==2.11.0 torchvision==0.26.0 --index-url https://download.pytorch.org/whl/cu128
& $python -m pip install numpy==2.3.2
Write-Host 'Downloading and pinning Robust Video Matting...'
if (-not (Test-Path (Join-Path $rvm '.git'))) {
    git clone https://github.com/PeterL1n/RobustVideoMatting.git $rvm
}
git -C $rvm fetch --depth 1 origin $commit
git -C $rvm checkout --detach $commit
[System.IO.File]::WriteAllText((Join-Path $runtime 'RVM_COMMIT'), $commit)
$weights = @(
    @{ Name='rvm_mobilenetv3.pth'; Hash='3C7C1D92033F7C38D6577C481D13A195D7D80A159B960F4F3119AC7B534CF4F8' },
    @{ Name='rvm_resnet50.pth'; Hash='C191A807251164C073DCE5FA408E7A816070D539B882B2A3150330A9FEC112CE' }
)
foreach ($weight in $weights) {
    $path = Join-Path $checkpoints $weight.Name
    Get-VerifiedDownload `
        "https://github.com/PeterL1n/RobustVideoMatting/releases/download/v1.0.0/$($weight.Name)" `
        $path $weight.Hash
}
function Install-Sam2 {
    $sam2Commit = '2b90b9f5ceec907a1c18123530e92e794ad901a4'
    $sam2 = Join-Path $runtime 'sam2'
    $sam2Marker = Join-Path $runtime 'SAM2_COMMIT'
    Remove-Item -LiteralPath $sam2Marker -Force -ErrorAction SilentlyContinue
    Write-Host 'Installing shared SAM2 video mask tracking...'
    & $python -m pip install triton-windows==3.6.0.post26
    & $python -m pip install iopath==0.1.10 hydra-core==1.3.2 tqdm==4.67.1
    if (-not (Test-Path (Join-Path $sam2 '.git'))) {
        git clone https://github.com/facebookresearch/sam2.git $sam2
    }
    git -C $sam2 fetch --depth 1 origin $sam2Commit
    git -C $sam2 checkout --detach $sam2Commit
    $env:SAM2_BUILD_CUDA = '0'
    & $python -m pip install --no-deps --editable $sam2
    if ($LASTEXITCODE -ne 0) { throw 'SAM2 installation failed.' }
    Get-VerifiedDownload `
        'https://huggingface.co/facebook/sam2.1-hiera-base-plus/resolve/b7320756a13354e7530a63935656d35b2f91a290/sam2.1_hiera_base_plus.pt?download=true' `
        (Join-Path $checkpoints 'sam2.1_hiera_base_plus.pt') `
        'A2345AEDE8715AB1D5D31B4A509FB160C5A4AF1970F199D9054CCFB746C004C5'
    Get-VerifiedDownload `
        'https://huggingface.co/facebook/sam2.1-hiera-small/resolve/ee5bba1d82bb8749febdf90f45e84b687142ba03/sam2.1_hiera_small.pt?download=true' `
        (Join-Path $checkpoints 'sam2.1_hiera_small.pt') `
        '6D1AA6F30DE5C92224F8172114DE081D104BBD23DD9DC5C58996F0CAD5DC4D38'
    Get-VerifiedDownload `
        'https://huggingface.co/facebook/sam2.1-hiera-tiny/resolve/de431c4043854a71d8101e17995dfe596bf101a5/sam2.1_hiera_tiny.pt?download=true' `
        (Join-Path $checkpoints 'sam2.1_hiera_tiny.pt') `
        '7402E0D864FA82708A20FBD15BC84245C2F26DFF0EB43A4B5B93452DEB34BE69'
    & $python -c "from sam2.build_sam import build_sam2_video_predictor; print('SAM2 import verified')"
    if ($LASTEXITCODE -ne 0) { throw 'SAM2 verification failed.' }
    $compileTest = @'
import torch
if torch.cuda.is_available():
    from torch.utils._triton import has_triton
    assert has_triton(), "Triton is unavailable"
    model = torch.compile(torch.nn.Conv2d(3, 8, 3, padding=1).cuda().eval(),
                          mode="max-autotune")
    with torch.inference_mode(), torch.autocast("cuda", dtype=torch.bfloat16):
        for _ in range(2):
            torch.compiler.cudagraph_mark_step_begin()
            model(torch.randn(1, 3, 32, 32, device="cuda"))
    torch.cuda.synchronize()
print("SAM2 compiler verified")
'@
    $env:TORCHINDUCTOR_CACHE_DIR = Join-Path $runtime 'torchinductor-cache'
    # Feed multiline verification code on stdin. Passing a here-string to
    # `python -c` lets Windows PowerShell strip embedded quotes.
    $compileTest | & $python -
    if ($LASTEXITCODE -ne 0) { throw 'SAM2 compiler verification failed.' }
    [System.IO.File]::WriteAllText($sam2Marker, $sam2Commit)
}
if ($InstallMatAnyone2 -or $InstallVideoMaMa -or $InstallViTMatte) { Install-Sam2 }
if ($InstallTransNetV2) {
    $transNetCommit = '85cef72af9a916bdfd7cc94a670c9cdfbf12d1ed'
    $transNet = Join-Path $runtime 'transnetv2'
    New-Item -ItemType Directory -Force -Path $transNet | Out-Null
    $transNetFiles = @(
        @{ Name='transnetv2_pytorch.py'; Uri="https://raw.githubusercontent.com/soCzech/TransNetV2/$transNetCommit/inference-pytorch/transnetv2_pytorch.py"; Hash='F7C1D437465579A8EC28A5ADD19853D2CB2755248EA4A4207678210A609428E1' },
        @{ Name='LICENSE'; Uri="https://raw.githubusercontent.com/soCzech/TransNetV2/$transNetCommit/LICENSE"; Hash='A8D7A056688CCEDEBE89F18FD60F1A47128DF94CB82669CD02459934919CBB6F' },
        @{ Name='transnetv2-pytorch-weights.pth'; Uri='https://huggingface.co/MiaoshouAI/transnetv2-pytorch-weights/resolve/fd36b849e53769133ae35d464581f0bcb41cade4/transnetv2-pytorch-weights.pth?download=true'; Hash='46520D66D4BF60414A4D82E0E94A92442FF950E34517A3718B2E54815E642B53' }
    )
    Write-Host 'Installing optional TransNetV2 scene detection...'
    foreach ($file in $transNetFiles) {
        $path = Join-Path $transNet $file.Name
        Get-VerifiedDownload $file.Uri $path $file.Hash
    }
    [System.IO.File]::WriteAllText((Join-Path $runtime 'TRANSNETV2_COMMIT'), $transNetCommit)
    $transNetWeights = Join-Path $transNet 'transnetv2-pytorch-weights.pth'
    & $python -c "import sys, torch; sys.path.insert(0, sys.argv[1]); from transnetv2_pytorch import TransNetV2; model=TransNetV2(); model.load_state_dict(torch.load(sys.argv[2], map_location='cpu', weights_only=True)); print('TransNetV2 model verified')" $transNet $transNetWeights
    if ($LASTEXITCODE -ne 0) { throw 'TransNetV2 model verification failed.' }
}
if ($InstallOmniShotCut) {
    $omniCommit = '23ad6fb41b296fb9258b0e7825125a914573b906'
    $omniRevision = '7f646c4ff4bb843e18c013481fb5d9ed2b068c6b'
    $omni = Join-Path $runtime 'omnishotcut'
    $omniMarker = Join-Path $runtime 'OMNISHOTCUT_COMMIT'
    Remove-Item -LiteralPath $omniMarker -Force -ErrorAction SilentlyContinue
    Write-Host 'Installing optional OmniShotCut modern transition detection...'
    & $python -m pip install ffmpeg-python==0.2.0 opencv-python-headless==5.0.0.93 `
        huggingface_hub==0.36.2 Pillow==12.2.0 packaging==26.3
    if ($LASTEXITCODE -ne 0) { throw 'OmniShotCut Python dependency installation failed.' }
    if (-not (Test-Path (Join-Path $omni '.git'))) {
        git clone https://github.com/UVA-Computer-Vision-Lab/OmniShotCut.git $omni
        if ($LASTEXITCODE -ne 0) { throw 'OmniShotCut repository clone failed.' }
    }
    git -C $omni fetch --depth 1 origin $omniCommit
    if ($LASTEXITCODE -ne 0) { throw 'OmniShotCut repository fetch failed.' }
    git -C $omni checkout --detach $omniCommit
    if ($LASTEXITCODE -ne 0) { throw 'OmniShotCut repository checkout failed.' }
    $omniHead = (git -C $omni rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $omniHead -ne $omniCommit) {
        throw "OmniShotCut revision validation failed: $omniHead"
    }
    & $python -m pip install --no-deps --editable $omni
    if ($LASTEXITCODE -ne 0) { throw 'OmniShotCut package installation failed.' }
    $omniCheckpoint = Join-Path $checkpoints 'OmniShotCut_ckpt.pth'
    Get-VerifiedDownload `
        "https://huggingface.co/uva-cv-lab/OmniShotCut/resolve/$omniRevision/OmniShotCut_ckpt.pth?download=true" `
        $omniCheckpoint '5948EA78E00626C0E6C5E742E64873EF872CF4A5071D2A0841AED51C3E686CFA'
    $omniTest = @'
import sys, numpy as np, torch
if not torch.cuda.is_available():
    raise RuntimeError("OmniShotCut currently requires NVIDIA CUDA")
sys.path.insert(0, sys.argv[1])
from omnishotcut.engine import load_model, _run_on_numpy
model, args = load_model(sys.argv[2])
frames = np.zeros((args.max_process_window_length, args.process_height, args.process_width, 3), dtype=np.uint8)
ranges, intra, inter = _run_on_numpy(frames, model, args, 20)
assert len(ranges) == len(intra) == len(inter)
print("OmniShotCut CUDA inference verified")
'@
    # stdin preserves the Python string literals under Windows PowerShell;
    # `python -c $omniTest` strips their double quotes during native binding.
    $omniTest | & $python - $omni $omniCheckpoint
    if ($LASTEXITCODE -ne 0) { throw 'OmniShotCut model verification failed.' }
    [System.IO.File]::WriteAllText($omniMarker, $omniCommit)
}
if ($InstallMatAnyone2) {
    $matAnyoneCommit = '0079197acd6d16a741f71558809c06c586c579e0'
    $matAnyone = Join-Path $runtime 'matanyone2'
    $matAnyoneMarker = Join-Path $runtime 'MATANYONE2_COMMIT'
    Remove-Item -LiteralPath $matAnyoneMarker -Force -ErrorAction SilentlyContinue
    Write-Host 'Installing optional MatAnyone 2 with interactive SAM2 masking...'
    & $python -m pip install hydra-core==1.3.2 huggingface_hub==0.36.2 `
        opencv-python-headless==5.0.0.93 imageio==2.25.0 tqdm==4.67.1
    if (-not (Test-Path (Join-Path $matAnyone '.git'))) {
        git clone https://github.com/pq-yang/MatAnyone2.git $matAnyone
    }
    git -C $matAnyone fetch --depth 1 origin $matAnyoneCommit
    git -C $matAnyone checkout --detach $matAnyoneCommit
    $matAnyoneWeights = Join-Path $checkpoints 'matanyone2.pth'
    Get-VerifiedDownload `
        'https://github.com/pq-yang/MatAnyone2/releases/download/v1.0.0/matanyone2.pth' `
        $matAnyoneWeights '5E9821E4087231427376B437C85BB6E072B41E582314F06FD524F75BC4AF5914'
    & $python -c "import sys, torch; sys.path.insert(0, sys.argv[1]); from matanyone2.utils.get_default_model import get_matanyone2_model; model=get_matanyone2_model(sys.argv[2], torch.device('cuda')); print('MatAnyone 2 model verified')" $matAnyone $matAnyoneWeights
    if ($LASTEXITCODE -ne 0) { throw 'MatAnyone 2 model verification failed.' }
    [System.IO.File]::WriteAllText($matAnyoneMarker, $matAnyoneCommit)
}
if ($InstallViTMatte) {
    $smallRevision = '6a58ad7646403c1df626fbd746900aec7361ea1d'
    $baseRevision = 'bf486d01a7d9e3dbcc8400f7942835caf0eaf76e'
    $small = Join-Path $runtime 'vitmatte-s'
    $base = Join-Path $runtime 'vitmatte-b'
    Write-Host 'Installing optional ViTMatte-S and ViTMatte-B...'
    & $python -m pip install transformers==4.57.0 safetensors==0.6.2 `
        opencv-python-headless==5.0.0.93
    if ($LASTEXITCODE -ne 0) { throw 'ViTMatte Python package installation failed.' }
    New-Item -ItemType Directory -Force -Path $small,$base | Out-Null
    $downloads = @(
        @{ Uri="https://huggingface.co/hustvl/vitmatte-small-composition-1k/resolve/$smallRevision/config.json?download=true"; Path=(Join-Path $small 'config.json'); Hash='AE1006F5A83227048B563B2E60709D4203E432B2276949EBEF41A8CFEEEAF45F' },
        @{ Uri="https://huggingface.co/hustvl/vitmatte-small-composition-1k/resolve/$smallRevision/preprocessor_config.json?download=true"; Path=(Join-Path $small 'preprocessor_config.json'); Hash='0DB558038B96A3F5C97E46D4EC8966FCC479E9AA58A391BCA60B5094A5F7FEE0' },
        @{ Uri="https://huggingface.co/hustvl/vitmatte-small-composition-1k/resolve/$smallRevision/model.safetensors?download=true"; Path=(Join-Path $small 'model.safetensors'); Hash='BDA9289DB1BB6762D978B42D1C62AE3F34DAF7497171A347A1D09657EFD788CB' },
        @{ Uri="https://huggingface.co/hustvl/vitmatte-base-composition-1k/resolve/$baseRevision/config.json?download=true"; Path=(Join-Path $base 'config.json'); Hash='67D70E8CBD850ADFDE5714AF6E1B4078CD266B8867A362CDF6023F1F9E045634' },
        @{ Uri="https://huggingface.co/hustvl/vitmatte-base-composition-1k/resolve/$baseRevision/preprocessor_config.json?download=true"; Path=(Join-Path $base 'preprocessor_config.json'); Hash='0DB558038B96A3F5C97E46D4EC8966FCC479E9AA58A391BCA60B5094A5F7FEE0' },
        @{ Uri="https://huggingface.co/hustvl/vitmatte-base-composition-1k/resolve/$baseRevision/pytorch_model.bin?download=true"; Path=(Join-Path $base 'pytorch_model.bin'); Hash='B2521BCC4B719FB24611C39605B6642162FD7502E69B3CC846506CA921757B41' }
    )
    foreach ($download in $downloads) {
        Get-VerifiedDownload $download.Uri $download.Path $download.Hash
    }
    foreach ($model in @($small, $base)) {
        & $python -c "from transformers import VitMatteForImageMatting, VitMatteImageProcessor; import sys; VitMatteImageProcessor.from_pretrained(sys.argv[1],local_files_only=True); VitMatteForImageMatting.from_pretrained(sys.argv[1],local_files_only=True); print('ViTMatte model verified')" $model
        if ($LASTEXITCODE -ne 0) { throw 'ViTMatte model verification failed.' }
    }
    [System.IO.File]::WriteAllText((Join-Path $runtime 'VITMATTE_S_REVISION'), $smallRevision)
    [System.IO.File]::WriteAllText((Join-Path $runtime 'VITMATTE_B_REVISION'), $baseRevision)
}
if ($InstallVideoMaMa) {
    $videoMaMaCommit = 'd5cce3e0ffe3b6429c147e658bb28bcfb576374c'
    $svdRevision = '9e43909513c6714f1bc78bcb44d96e733cd242aa'
    $modelRevision = 'e289a7acc8403c4fbe4dea2a1de5a9749ebc9bf5'
    $videoMaMa = Join-Path $runtime 'videomama'
    $svd = Join-Path $runtime 'videomama-base'
    $videoMaMaModel = Join-Path $runtime 'videomama-model'
    $videoMaMaMarker = Join-Path $runtime 'VIDEOMAMA_COMMIT'
    Remove-Item -LiteralPath $videoMaMaMarker -Force -ErrorAction SilentlyContinue
    Write-Host 'Installing optional VideoMaMa high-quality matting and SAM2...'
    & $python -m pip install diffusers==0.35.2 transformers==4.57.0 `
        accelerate==1.10.1 einops==0.8.1 safetensors==0.6.2
    if ($LASTEXITCODE -ne 0) { throw 'VideoMaMa Python package installation failed.' }
    if (-not (Test-Path (Join-Path $videoMaMa '.git'))) {
        git clone https://github.com/cvlab-kaist/VideoMaMa.git $videoMaMa
    }
    git -C $videoMaMa fetch --depth 1 origin $videoMaMaCommit
    git -C $videoMaMa checkout --detach $videoMaMaCommit
    New-Item -ItemType Directory -Force -Path `
        (Join-Path $svd 'feature_extractor'),(Join-Path $svd 'image_encoder'), `
        (Join-Path $svd 'vae'),(Join-Path $videoMaMaModel 'unet') | Out-Null
    $downloads = @(
        @{ Uri="https://huggingface.co/stabilityai/stable-video-diffusion-img2vid-xt/resolve/$svdRevision/feature_extractor/preprocessor_config.json?download=true"; Path=(Join-Path $svd 'feature_extractor\preprocessor_config.json'); Hash='4DB495644E3E5BD8FCAC52F70E7FC0B413C911086021ACF73AC30E5911166E95' },
        @{ Uri="https://huggingface.co/stabilityai/stable-video-diffusion-img2vid-xt/resolve/$svdRevision/image_encoder/config.json?download=true"; Path=(Join-Path $svd 'image_encoder\config.json'); Hash='65DA4496F116D2B297FE864E0F31242FBC57E26A5D95B93310F2034E1E90D0EC' },
        @{ Uri="https://huggingface.co/stabilityai/stable-video-diffusion-img2vid-xt/resolve/$svdRevision/image_encoder/model.fp16.safetensors?download=true"; Path=(Join-Path $svd 'image_encoder\model.fp16.safetensors'); Hash='AE616C24393DD1854372B0639E5541666F7521CBE219669255E865CB7F89466A' },
        @{ Uri="https://huggingface.co/stabilityai/stable-video-diffusion-img2vid-xt/resolve/$svdRevision/vae/config.json?download=true"; Path=(Join-Path $svd 'vae\config.json'); Hash='8F34272DB69F7E2C615DA6142CA3F9FDCD7B682BCFD903CEB15035FEA79A8303' },
        @{ Uri="https://huggingface.co/stabilityai/stable-video-diffusion-img2vid-xt/resolve/$svdRevision/vae/diffusion_pytorch_model.fp16.safetensors?download=true"; Path=(Join-Path $svd 'vae\diffusion_pytorch_model.fp16.safetensors'); Hash='AF602CD0EB4AD6086EC94FBF1438DFB1BE5EC9AC03FD0215640854E90D6463A3' },
        @{ Uri="https://huggingface.co/SammyLim/VideoMaMa/resolve/$modelRevision/unet/config.json?download=true"; Path=(Join-Path $videoMaMaModel 'unet\config.json'); Hash='D93F866DAA31851058CA16A18E35B22DC9D3655D61E991B67D120FF333BF8176' },
        @{ Uri="https://huggingface.co/SammyLim/VideoMaMa/resolve/$modelRevision/unet/diffusion_pytorch_model.safetensors?download=true"; Path=(Join-Path $videoMaMaModel 'unet\diffusion_pytorch_model.safetensors'); Hash='F2442BF16EDEDAD25C1C272AE7535B6411C43CEE5C27B012BB6F7FDA72D07B8C' }
    )
    foreach ($download in $downloads) {
        Get-VerifiedDownload $download.Uri $download.Path $download.Hash
    }
    & $python -c "import sys, torch; sys.path.insert(0, sys.argv[1]); from pipeline_svd_mask import VideoInferencePipeline; from sam2.build_sam import build_sam2_video_predictor; assert torch.cuda.is_available(), 'VideoMaMa requires NVIDIA CUDA'; print('VideoMaMa and SAM2 imports verified')" $videoMaMa
    if ($LASTEXITCODE -ne 0) { throw 'VideoMaMa verification failed.' }
    [System.IO.File]::WriteAllText($videoMaMaMarker, $videoMaMaCommit)
    [System.IO.File]::WriteAllText((Join-Path $runtime 'VIDEOMAMA_SVD_REVISION'), $svdRevision)
    [System.IO.File]::WriteAllText((Join-Path $runtime 'VIDEOMAMA_MODEL_REVISION'), $modelRevision)
}
if ($InstallProPainter) {
    $proPainterCommit = 'c8983a445720450bf2fd976cab0adb1cad19547d'
    $proPainter = Join-Path $runtime 'propainter-streaming'
    $proPainterWeights = Join-Path $runtime 'propainter-streaming-weights'
    $proPainterMarker = Join-Path $runtime 'PROPAINTER_STREAMING_COMMIT'
    Remove-Item -LiteralPath $proPainterMarker -Force -ErrorAction SilentlyContinue
    Write-Host 'Installing optional streaming ProPainter video object removal (non-commercial use only)...'
    & $python -m pip install addict==2.4.0 future==1.0.0 `
        scipy==1.16.1 opencv-python-headless==5.0.0.93 matplotlib==3.10.5 `
        scikit-image==0.25.2 imageio-ffmpeg==0.6.0 pyyaml==6.0.2 `
        requests==2.32.4 timm==1.0.19 tqdm==4.67.1 pytorchcv==0.0.74
    if ($LASTEXITCODE -ne 0) { throw 'ProPainter Python package installation failed.' }
    if (-not (Test-Path (Join-Path $proPainter '.git'))) {
        git clone https://github.com/osmr/propainter.git $proPainter
    }
    git -C $proPainter fetch --depth 1 origin $proPainterCommit
    git -C $proPainter checkout --detach $proPainterCommit
    New-Item -ItemType Directory -Force -Path $proPainterWeights | Out-Null
    & $python -c "import sys; sys.path.insert(0,sys.argv[1]); from propainter.propainter_video import ProPainterIterator; from pytorchcv.models.raft import raft_things; from pytorchcv.models.propainter_rfc import propainter_rfc; from pytorchcv.models.propainter import propainter; root=sys.argv[2]; models=[raft_things(pretrained=True,root=root,in_normalize=False,iters=20),propainter_rfc(pretrained=True,root=root),propainter(pretrained=True,root=root)]; print('Streaming ProPainter models verified')" $proPainter $proPainterWeights
    if ($LASTEXITCODE -ne 0) { throw 'ProPainter verification failed.' }
    [System.IO.File]::WriteAllText($proPainterMarker, $proPainterCommit)
}
Write-Host "Setup complete. In QuickPlayer Custom Show Settings, select: $python"
