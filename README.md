# LethalMimics

A Lethal Company mod where enemies use your crew's voices to lure them in, plus an experimental AI add-on that clones voices and lets enemies talk back.

| | What | Where to get it |
|---|---|---|
| **LethalMimics** | Players record short voice clips; enemies play them back as teammates who aren't there. Everyone in the lobby needs it. | [Thunderstore](https://thunderstore.io/c/lethal-company/p/Deja/LethalMimics/) |
| **LethalMimics AI** | Optional, **host-only, experimental**. Clones every player's voice with local AI models (Qwen3-TTS, Whisper, a small LLM) on the host's PC and lets enemies speak and reply as them. Needs an NVIDIA GPU and several GB of downloads. **Use at your own risk.** | [Releases](https://github.com/BKPatt/LethalMimics/releases) (`LethalMimicsAI-*.zip`) |

- How the mod works and its settings: [package/README.md](package/README.md)
- AI add-on install, setup and model choices: [addon/package/README.md](addon/package/README.md)

## Privacy
- Recorded clips are stored in `%LOCALAPPDATA%\LethalMimics\clips`, outside the mod profile.
- Players are only ever asked for live voice when the host runs the AI add-on with AI switched on. They get an on-screen notice and can opt out with `ShareVoiceWithHostAi = false`.
- The add-on processes voice only on the host's PC. Its voice cache (off by default), Python environment and any LLM API key live in `%LOCALAPPDATA%\LethalMimics`, never in the profile's config folder.

## Building
Requires the .NET SDK and a Lethal Company install with BepInEx (a mod-manager profile works).

1. Copy `LethalMimics.local.props.example` to `LethalMimics.local.props` and set `GameDir` (and optionally `ProfileDir`).
2. Run `.\pack.ps1` to build both plugins and create `dist\LethalMimics-<ver>.zip` (Thunderstore) and `dist\LethalMimicsAI-<ver>.zip` (GitHub release). Add `-Deploy` to also install both into `ProfileDir`.

Layout:
- `src/`, `package/` – the Thunderstore mod. It contains no AI code; it exposes `MimicsApi` / `IMimicVoiceAi` for add-ons.
- `addon/src/` – the AI add-on plugin (talks to the local server over `127.0.0.1`).
- `addon/ai_server/` – the Python server (`server.py`), `requirements.txt` and `setup.ps1`.
