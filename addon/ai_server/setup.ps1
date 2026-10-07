<#
LethalMimics AI add-on - one-time setup (host only).

Creates a Python environment with PyTorch (CUDA), Qwen3-TTS (faster-qwen3-tts) and Whisper, downloads the models,
and pulls the default LLM into Ollama. Nobody but the host needs this.

Run: right-click > "Run with PowerShell", or from a terminal:
    powershell -ExecutionPolicy Bypass -File setup.ps1

Options:
    -VenvDir   Where to create the environment (default %LOCALAPPDATA%\LethalMimics\venv, which survives mod updates;
               the add-on looks there automatically).
    -Cuda      PyTorch CUDA build: cu128 (default, RTX 20xx-50xx) or cu126 for older drivers.
    -TtsModel / -AsrModel / -LlmModel  Models to download (match the add-on's config if you changed them).
    -SkipModels / -SkipOllama  Skip those steps.
#>
param(
    [string]$VenvDir = (Join-Path $env:LOCALAPPDATA "LethalMimics\venv"),
    [string]$Cuda = "cu128",
    [string]$TtsModel = "Qwen/Qwen3-TTS-12Hz-1.7B-Base",
    [string]$AsrModel = "openai/whisper-large-v3-turbo",
    [string]$LlmModel = "hf.co/openbmb/MiniCPM5-2B-GGUF:Q8_0",
    [switch]$SkipModels,
    [switch]$SkipOllama,
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot

function Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Pause-Exit { if (-not $NoPause) { Read-Host "Press Enter to exit" | Out-Null } }
function Fail($msg) { Write-Host "`nERROR: $msg" -ForegroundColor Red; Pause-Exit; exit 1 }

Step "Checking GPU"
$gpu = $null
try { $gpu = & nvidia-smi --query-gpu=name,memory.total --format=csv,noheader 2>$null } catch {}
if ($gpu) { Write-Host "Found: $gpu" }
else {
    Write-Host "No NVIDIA GPU detected. Voice cloning needs an NVIDIA GPU (8 GB+ VRAM recommended; it runs alongside the game)." -ForegroundColor Yellow
    if ($NoPause -or (Read-Host "Continue anyway? (y/N)") -ne "y") { exit 1 }
}

Step "Checking uv (Python installer)"
$uv = Get-Command uv -ErrorAction SilentlyContinue
if (-not $uv) {
    Write-Host "uv is not installed. It downloads the right Python version and packages quickly. See https://docs.astral.sh/uv/"
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { Fail "Install uv from https://docs.astral.sh/uv/getting-started/installation/ then run this again." }
    if ($NoPause -or (Read-Host "Install uv now with winget? (y/N)") -ne "y") { Fail "uv is required. Install it, then run this again." }
    & winget install --id astral-sh.uv -e --accept-source-agreements --accept-package-agreements
    $env:Path = [Environment]::GetEnvironmentVariable("Path", "User") + ";" + [Environment]::GetEnvironmentVariable("Path", "Machine") + ";$env:Path"
    $uv = Get-Command uv -ErrorAction SilentlyContinue
    if (-not $uv) { Fail "uv was installed but isn't on PATH yet. Open a new terminal and run this script again." }
}

Step "Creating Python 3.12 environment at $VenvDir"
New-Item -ItemType Directory -Force (Split-Path $VenvDir) | Out-Null
& uv venv $VenvDir --python 3.12 --allow-existing
if ($LASTEXITCODE -ne 0) { Fail "Could not create the environment." }
$py = Join-Path $VenvDir "Scripts\python.exe"

Step "Installing PyTorch ($Cuda) - large download"
& uv pip install --python $py --index-url "https://download.pytorch.org/whl/$Cuda" torch torchaudio
if ($LASTEXITCODE -ne 0) { Fail "PyTorch install failed." }

Step "Installing Qwen3-TTS, Whisper and audio libraries"
& uv pip install --python $py --extra-index-url "https://download.pytorch.org/whl/$Cuda" --index-strategy unsafe-best-match -r (Join-Path $here "requirements.txt")
if ($LASTEXITCODE -ne 0) { Fail "Package install failed." }

& $py -c "import torch; print('PyTorch', torch.__version__, '| CUDA available:', torch.cuda.is_available())"

if (-not $SkipModels) {
    Step "Downloading models (~6 GB): $TtsModel, $AsrModel"
    $env:HF_HUB_DISABLE_IMPLICIT_TOKEN = "1"   # public models; a stale saved login token would break downloads
    $env:HF_HUB_DISABLE_SYMLINKS_WARNING = "1"
    & $py -c "import sys; from huggingface_hub import snapshot_download as d; [print('ok', d(m)) for m in sys.argv[1:]]" $TtsModel $AsrModel
    if ($LASTEXITCODE -ne 0) { Fail "Model download failed." }
}

if (-not $SkipOllama) {
    Step "Pulling the LLM into Ollama: $LlmModel"
    $ollama = Get-Command ollama -ErrorAction SilentlyContinue
    if ($ollama) {
        & ollama pull $LlmModel
        if ($LASTEXITCODE -ne 0) { Write-Host "ollama pull failed - is Ollama running?" -ForegroundColor Yellow }
    } else {
        Write-Host "Ollama not found. Install it from https://ollama.com then run:  ollama pull $LlmModel" -ForegroundColor Yellow
        Write-Host "(Or point LlmApi/LlmBaseUrl/LlmModel in the add-on config at any OpenAI-compatible server such as LM Studio.)"
    }
}

Step "Done"
Write-Host "Now set  [AI Voice Cloning (host only)] Enabled = true  in BepInEx/config/brant.LethalMimics.AI.cfg and host a lobby."
if ($VenvDir -ne (Join-Path $env:LOCALAPPDATA "LethalMimics\venv")) {
    Write-Host "Custom location: set PythonPath = $py" -ForegroundColor Yellow
}
Pause-Exit
