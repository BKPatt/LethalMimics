using System;
using System.IO;
using BepInEx.Configuration;

namespace LethalMimics.AI;

internal sealed class AiConfig
{
    public readonly ConfigEntry<bool> Enabled;
    public readonly ConfigEntry<string> ToggleKey;
    public readonly ConfigEntry<string> ServerUrl;
    public readonly ConfigEntry<bool> AutoStartServer;
    public readonly ConfigEntry<string> PythonPath;
    public readonly ConfigEntry<string> ServerScript;
    public readonly ConfigEntry<string> TtsModel;
    public readonly ConfigEntry<string> AsrModel;
    public readonly ConfigEntry<string> LlmApi;
    public readonly ConfigEntry<string> LlmBaseUrl;
    public readonly ConfigEntry<string> LlmModel;
    public readonly ConfigEntry<string> Device;
    public readonly ConfigEntry<string> ModelDownloadDir;
    public readonly ConfigEntry<string> Language;
    public readonly ConfigEntry<float> MaxReferenceSeconds;
    public readonly ConfigEntry<float> MinReferenceSeconds;
    public readonly ConfigEntry<bool> CacheVoicesAcrossGames;
    public readonly ConfigEntry<string> VoiceCacheDir;
    public readonly ConfigEntry<float> LineChance;
    public readonly ConfigEntry<float> ReplyChance;
    public readonly ConfigEntry<float> ReplyWindowSeconds;
    public readonly ConfigEntry<bool> ReplyToNameMentions;

    /// <summary>
    /// Per-user data (Python environment, voice cache, LLM API key). Deliberately outside the mod-manager profile:
    /// profile exports include the config folder, and these must never be shared.
    /// </summary>
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LethalMimics");

    public string PluginDir => Path.GetDirectoryName(typeof(AiPlugin).Assembly.Location);

    public AiConfig(ConfigFile cfg)
    {
        const string ai = "AI Voice Cloning (host only)";
        Enabled = cfg.Bind(ai, "Enabled", false,
            "Clone players' voices and let a small local LLM speak and reply as them. Only does anything when you host. " +
            "Everything runs on your computer. Needs the AI server set up (see README).");
        ToggleKey = cfg.Bind(ai, "ToggleKey", "<Keyboard>/home",
            "Press in game while hosting to switch AI voice cloning on/off for the current lobby without leaving (players are notified). " +
            "Works even if Enabled is false (turning it on starts the AI server). Empty = disabled.");
        ServerUrl = cfg.Bind(ai, "ServerUrl", "http://127.0.0.1:5279", "Address of the AI server.");
        AutoStartServer = cfg.Bind(ai, "AutoStartServer", true,
            "Start the AI server automatically if it isn't already running at ServerUrl. It shuts down when the game closes.");
        PythonPath = cfg.Bind(ai, "PythonPath", "",
            "python.exe to run the server with. Empty = %LOCALAPPDATA%/LethalMimics/venv (where setup.ps1 puts it), " +
            "else <plugin folder>/ai_server/.venv");
        ServerScript = cfg.Bind(ai, "ServerScript", "", "Path to server.py. Empty = <plugin folder>/ai_server/server.py");
        TtsModel = cfg.Bind(ai, "TtsModel", "Qwen/Qwen3-TTS-12Hz-1.7B-Base",
            "Voice-cloning TTS model: Hugging Face repo id or local folder. Must be a Qwen3-TTS *Base* model.");
        AsrModel = cfg.Bind(ai, "AsrModel", "openai/whisper-large-v3-turbo",
            "Speech-to-text model: Hugging Face repo id or local folder (any Whisper-style transformers model).");
        LlmApi = cfg.Bind(ai, "LlmApi", "ollama",
            new ConfigDescription("Kind of LLM endpoint.", new AcceptableValueList<string>("ollama", "openai")));
        LlmBaseUrl = cfg.Bind(ai, "LlmBaseUrl", "http://127.0.0.1:11434",
            "LLM endpoint. Ollama default, or any OpenAI-compatible server (LM Studio, llama.cpp, vLLM...) with LlmApi=openai. " +
            "If it needs an API key, put the key in %LOCALAPPDATA%/LethalMimics/llm_api_key.txt or the LETHALMIMICS_LLM_API_KEY " +
            "environment variable - never in this file, because config folders are included in profile exports.");
        LlmModel = cfg.Bind(ai, "LlmModel", "hf.co/openbmb/MiniCPM5-2B-GGUF:Q8_0",
            "LLM model name or Ollama tag. A small, recent instruction-tuned chat model (about 1-4B) works best.");
        Device = cfg.Bind(ai, "Device", "cuda:0", "Torch device for TTS and speech recognition (cuda:0, cuda:1, cpu).");
        ModelDownloadDir = cfg.Bind(ai, "ModelDownloadDir", "", "Where to download models (HF_HOME). Empty = Hugging Face default cache.");
        Language = cfg.Bind(ai, "Language", "English", "Language for speech recognition and TTS (English, Chinese, Japanese, ... or Auto).");
        MaxReferenceSeconds = cfg.Bind(ai, "MaxReferenceSeconds", 12f,
            new ConfigDescription("Most clean speech used per voice clone. Qwen3-TTS gains little past ~10-15s and every line gets slower " +
                                  "with a longer reference. Once reached, cloning keeps improving by swapping in cleaner audio.",
                new AcceptableValueRange<float>(3f, 30f)));
        MinReferenceSeconds = cfg.Bind(ai, "MinReferenceSeconds", 4f,
            new ConfigDescription("Clean speech needed before a player's voice is cloned at all.", new AcceptableValueRange<float>(3f, 15f)));
        CacheVoicesAcrossGames = cfg.Bind(ai, "CacheVoicesAcrossGames", false,
            "Keep players' voice samples, nicknames and speaking style on your disk (keyed by Steam ID) so clones are ready immediately next game.");
        VoiceCacheDir = cfg.Bind(ai, "VoiceCacheDir", "",
            "Folder for the voice cache. Empty = %LOCALAPPDATA%/LethalMimics/voice_cache (outside the profile, so it is never included in profile exports).");
        LineChance = cfg.Bind(ai, "AiLineChance", 0.5f,
            new ConfigDescription("When an enemy mimics someone with both recorded clips and a cloned voice, chance to say a new AI line instead of a clip.",
                new AcceptableValueRange<float>(0f, 1f)));
        ReplyChance = cfg.Bind(ai, "ReplyChance", 0.85f,
            new ConfigDescription("Chance an enemy answers when a nearby player talks to the person it is pretending to be.", new AcceptableValueRange<float>(0f, 1f)));
        ReplyWindowSeconds = cfg.Bind(ai, "ReplyWindowSeconds", 12f,
            new ConfigDescription("After an enemy mimics someone, how long a nearby player's speech counts as answering it.", new AcceptableValueRange<float>(2f, 60f)));
        ReplyToNameMentions = cfg.Bind(ai, "ReplyToNameMentions", true,
            "Also answer when a player near an enemy calls out another player's name or learned nickname (if that player is far away).");
    }
}
