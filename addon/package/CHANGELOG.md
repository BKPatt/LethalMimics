# Changelog

## 1.0.0
- Split out of LethalMimics as a separate, host-only add-on (GitHub only).
- Clones voices with a Qwen3-TTS Base model (CUDA-graph accelerated) and transcribes speech with a Whisper-style model.
- Screams and whispers are filtered out, and clones improve as players talk.
- A local LLM (Ollama or any OpenAI-compatible server) speaks and replies in each player's style and learns their nicknames.
- Voices can optionally be cached across games.
- Switch it on or off mid-lobby with `Home`; players are notified.
- The AI server starts Ollama and pulls the LLM automatically if needed.
- The LLM API key and the voice cache live in `%LOCALAPPDATA%\LethalMimics`, never in the mod profile.
