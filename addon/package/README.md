# LethalMimics AI (experimental add-on)

An optional, **host-only** add-on for [LethalMimics](https://thunderstore.io/c/lethal-company/p/Deja/LethalMimics/). It clones every player's voice with AI models running on the host's own PC, and lets enemies **talk back** as them.

> **Use at your own risk.** This add-on is not distributed on Thunderstore. It runs a local Python server with large AI models (several GB of downloads) and needs a capable NVIDIA GPU. Only the host installs it; everyone else just needs LethalMimics from Thunderstore.

## What it does

When you host with the add-on switched on:

- **Every player's normal voice chat** is used, not only recorded clips.
  - Each player's game sends their own speech to your PC. They see an on-screen notice first and can opt out with `ShareVoiceWithHostAi = false` in LethalMimics' config.
  - Only speech that others could already hear is sent: nothing while push-to-talk is released, and nothing while recording a clip.
- **Speech recognition and filtering:**
  - A speech-recognition model transcribes each line.
  - Screaming, shouting, whispering, clipping and noise are filtered out.
  - The cleanest stretches of each player's normal speaking voice are kept.
- **Clones improve during the game:**
  - A voice-cloning text-to-speech model clones each voice from that player's best ~12 s of clean speech. Past that point more audio barely helps, and every line gets slower to generate.
  - Once a player reaches 12 s, cleaner audio keeps replacing worse audio, so the clone keeps getting better.
- **Talks like the player:** a small local LLM writes lines in each player's own style, based on things they actually said.
- **Talks back:**
  - If you answer a mimic ("Jake? where are you?"), the enemy replies in Jake's cloned voice, usually within 2–3 seconds.
  - Calling out a far-away player's name near an enemy can also get an answer.
- **Learns names:** it works out the nicknames and real names people call each other and uses them in conversation.
- **Cross-game cache (optional):** keep voices, nicknames and speaking style on disk, keyed by Steam ID, so clones are ready as soon as the next game starts.

**In-game toggle:** while hosting, press **`Home`** (`ToggleKey`) to switch AI cloning off or on for the current lobby without leaving. Everyone gets a notice, and players stop sending their voice while it's off. The models stay loaded, so switching back on is instant. This works even when `Enabled = false`: pressing it starts the AI for that lobby.

**Privacy:** voice data is processed only on the host's PC and is only saved to disk if the host turns on `CacheVoicesAcrossGames`. The cache, the Python environment and any LLM API key live in `%LOCALAPPDATA%\LethalMimics`, outside the mod profile, so exporting or sharing a profile never includes them.

## Requirements
- Windows, and an NVIDIA GPU with enough free VRAM for your chosen models **while the game runs**. With the default models the AI uses about 7 GB (tested on an RTX 3090).
- Disk space for the Python environment (about 6 GB) plus your models (about 8 GB with the defaults).
- [Ollama](https://ollama.com) for the LLM, or any OpenAI-compatible server (LM Studio, llama.cpp, vLLM…).
- [LethalMimics](https://thunderstore.io/c/lethal-company/p/Deja/LethalMimics/) installed.

## Install
1. Install **LethalMimics** from Thunderstore in your mod manager.
2. Download `LethalMimicsAI-<version>.zip` from the [GitHub releases](https://github.com/BKPatt/LethalMimics/releases).
3. Add it to your profile, either way:
   - **r2modman / Thunderstore app:** *Settings → Import local mod*, pick the zip.
   - **By hand:** extract the zip's `BepInEx` folder into your profile folder (*Settings → Browse profile folder*).

## Set up the AI (one time)
Open the add-on's `ai_server` folder (in your profile: `BepInEx/plugins/…LethalMimicsAI/ai_server/`).

**Easy way:** right-click **`setup.ps1` → Run with PowerShell.** It installs uv (Python installer) with winget if needed, creates a Python 3.12 environment in `%LOCALAPPDATA%\LethalMimics\venv` with PyTorch (CUDA) and the TTS and speech-recognition libraries, downloads the default models and pulls the default LLM into Ollama. Install Ollama first if you want the LLM pulled too.

**By hand:** in PowerShell, in the `ai_server` folder:
```powershell
winget install --id astral-sh.uv -e        # then open a new PowerShell window
$py = "$env:LOCALAPPDATA\LethalMimics\venv\Scripts\python.exe"
uv venv "$env:LOCALAPPDATA\LethalMimics\venv" --python 3.12
uv pip install --python $py --index-url https://download.pytorch.org/whl/cu128 torch torchaudio
uv pip install --python $py --extra-index-url https://download.pytorch.org/whl/cu128 --index-strategy unsafe-best-match -r requirements.txt
```
`cu128` suits RTX 20xx to 50xx cards on current drivers; use `cu126` if PyTorch can't see your GPU. Models download automatically the first time you host (that lobby then waits several minutes).

Then:
1. In `BepInEx/config/brant.LethalMimics.AI.cfg`, set `[AI Voice Cloning (host only)] Enabled = true` (or just press `Home` in a lobby).
2. **Host a lobby.** The AI server starts automatically, and so does Ollama if it isn't running. The first lobby takes about 15–30 seconds to load the models; after that they stay loaded until you quit the game. The AI server shuts down with the game; Ollama keeps running like it normally does. The server log is `BepInEx/LethalMimics_ai_server.log`.

## Choosing models
The config's defaults are a tested set. You can swap any of them. `TtsModel` and `AsrModel` accept a Hugging Face model ID (`owner/name`) or a path to a local folder; they download automatically on first use.

- **Voice cloning (`TtsModel`):** must be a **Qwen3-TTS "Base" model**. Base models are the ones that clone a voice from a sample; the CustomVoice and VoiceDesign variants won't work. A smaller Base model uses less VRAM and responds faster, but the clone sounds less like the player.
- **Speech recognition (`AsrModel`):** any Whisper-style model that works with Hugging Face Transformers' speech-recognition pipeline. The large "turbo" versions are a good mix of accuracy and speed. Smaller ones save VRAM but mishear more names.
- **LLM (`LlmModel`, `LlmApi`, `LlmBaseUrl`):**
  - **With Ollama** (`LlmApi = ollama`): set `LlmModel` to any Ollama tag, or to a Hugging Face GGUF as `hf.co/<owner>/<repo>:<quant>`. If Ollama is running on this PC, the server pulls the model automatically.
  - **With another server** (`LlmApi = openai`): point `LlmBaseUrl` at any OpenAI-compatible server, such as LM Studio, llama.cpp or vLLM, and set `LlmModel` to the model name that server uses.
  - **API keys:** if your LLM server needs one, put it in `%LOCALAPPDATA%\LethalMimics\llm_api_key.txt` or the `LETHALMIMICS_LLM_API_KEY` environment variable. **Never put keys in the mod config** – config folders are included when profiles are exported and shared.
  - **Which LLM:** a small, recent instruction-tuned chat model of about 1–4B parameters works best. Bigger models write better lines but reply more slowly and take VRAM from the game.
- **GPU:** to keep the game smooth, run the AI on a second GPU by setting `Device` (for example `cuda:1`).

## Troubleshooting
- **"AI voices failed":** read `BepInEx/LethalMimics_ai_server.log`. "Python not found" means the setup hasn't been run, or `PythonPath` points to the wrong place.
- **The AI never speaks for someone:** that player needs about 4 s of clean, normal-volume speech first. Check that they haven't opted out.
- **Laggy game while hosting with AI:** pick smaller models, or set `Device` to a second GPU.
- **Downloads fail with 401:** a stale Hugging Face login is saved on your PC. The server already ignores saved logins for public models; for private models, run the server manually with `--hf-token`.
