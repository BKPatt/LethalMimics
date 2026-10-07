using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;

namespace LethalMimics;

internal sealed class MimicConfig
{
    // --- Recording (applies to whoever is recording) ---
    public readonly ConfigEntry<string> RecordKey;
    public readonly ConfigEntry<string> ClearClipsKey;
    public readonly ConfigEntry<float> MaxClipSeconds;
    public readonly ConfigEntry<float> MinClipSeconds;
    public readonly ConfigEntry<int> TransmitSampleRate;
    public readonly ConfigEntry<bool> TrimSilence;
    public readonly ConfigEntry<bool> SaveClips;
    public readonly ConfigEntry<int> MaxSavedClips;
    public readonly ConfigEntry<bool> ShowHud;
    public readonly ConfigEntry<string> DismissNoticeKey;

    // --- Mimics (only the host's values are used) ---
    public readonly ConfigEntry<bool> MimicsEnabled;
    public readonly ConfigEntry<int> MaxClipsPerPlayer;
    public readonly ConfigEntry<float> MinInterval;
    public readonly ConfigEntry<float> MaxInterval;
    public readonly ConfigEntry<float> ListenerCooldown;
    public readonly ConfigEntry<float> MinMimicShare;
    public readonly ConfigEntry<float> MimicChance;
    public readonly ConfigEntry<float> HearingRange;
    public readonly ConfigEntry<float> MinOwnerDistance;
    public readonly ConfigEntry<bool> AllowNearbyOwnerFallback;
    public readonly ConfigEntry<float> DeadOwnerWeight;
    public readonly ConfigEntry<float> PitchVariation;
    public readonly ConfigEntry<float> MinVolume;
    public readonly ConfigEntry<float> FragmentChance;
    public readonly ConfigEntry<float> RepeatChance;
    public readonly ConfigEntry<string> ExcludedEnemies;

    // --- Voice sharing with a host running the AI add-on: each player decides ---
    public readonly ConfigEntry<bool> ShareVoiceWithHostAi;
    public readonly ConfigEntry<int> AiStreamSampleRate;

    private HashSet<string> excludedCache;
    private string excludedCacheSource;

    public MimicConfig(ConfigFile cfg)
    {
        const string rec = "Recording";
        RecordKey = cfg.Bind(rec, "RecordKey", "<Keyboard>/j",
            "Hold to record a mimic clip, release to finish. Unity Input System path, e.g. <Keyboard>/j, <Keyboard>/h, <Mouse>/middleButton. " +
            "Your normal voice chat is muted while held.");
        ClearClipsKey = cfg.Bind(rec, "ClearClipsKey", "",
            "Optional key that deletes all of your recorded clips (local saves and everyone's copies). Empty = disabled. Example: <Keyboard>/f8");
        MaxClipSeconds = cfg.Bind(rec, "MaxClipSeconds", 8f,
            new ConfigDescription("Recording stops automatically after this many seconds.", new AcceptableValueRange<float>(1f, 20f)));
        MinClipSeconds = cfg.Bind(rec, "MinClipSeconds", 0.5f,
            new ConfigDescription("Clips shorter than this (after trimming silence) are discarded.", new AcceptableValueRange<float>(0.1f, 5f)));
        TransmitSampleRate = cfg.Bind(rec, "TransmitSampleRate", 16000,
            new ConfigDescription("Sample rate clips are sent at. Higher = better quality, more bandwidth.", new AcceptableValueList<int>(8000, 11025, 16000, 22050, 24000)));
        TrimSilence = cfg.Bind(rec, "TrimSilence", true, "Trim silence from the start and end of recordings.");
        SaveClips = cfg.Bind(rec, "SaveClips", true,
            "Save your clips to disk and re-upload them automatically when you join a lobby. They are kept in " +
            "%LOCALAPPDATA%/LethalMimics/clips, outside the mod profile, so exporting or sharing a profile never includes your voice.");
        MaxSavedClips = cfg.Bind(rec, "MaxSavedClips", 15,
            new ConfigDescription("How many of your own clips to keep. Oldest are dropped first.", new AcceptableValueRange<int>(1, 100)));
        ShowHud = cfg.Bind(rec, "ShowHud", true, "Show a recording indicator and short notices on screen.");
        DismissNoticeKey = cfg.Bind(rec, "DismissNoticeKey", "<Keyboard>/delete",
            "Press to hide the notice currently on screen (for example the AI voice sharing notice). Empty = disabled.");

        const string mim = "Mimics (host only)";
        MimicsEnabled = cfg.Bind(mim, "Enabled", true, "Allow enemies to play recorded voice clips.");
        MaxClipsPerPlayer = cfg.Bind(mim, "MaxClipsPerPlayer", 15,
            new ConfigDescription("How many clips per player the lobby keeps. Oldest are dropped first.", new AcceptableValueRange<int>(1, 100)));
        MinInterval = cfg.Bind(mim, "MinInterval", 25f,
            new ConfigDescription("Minimum seconds between mimics from the same enemy.", new AcceptableValueRange<float>(3f, 600f)));
        MaxInterval = cfg.Bind(mim, "MaxInterval", 80f,
            new ConfigDescription("Maximum seconds between mimics from the same enemy.", new AcceptableValueRange<float>(3f, 900f)));
        ListenerCooldown = cfg.Bind(mim, "ListenerCooldown", 7f,
            new ConfigDescription("After a player hears a mimic, no other enemy near them mimics for this many seconds. " +
                                  "Enemies in other parts of the level can still talk at the same time.", new AcceptableValueRange<float>(0f, 120f)));
        MinMimicShare = cfg.Bind(mim, "MinMimicShare", 0.5f,
            new ConfigDescription("Each level rolls what share of its enemies can mimic: somewhere between this and all of them. " +
                                  "1 = every enemy, every level.", new AcceptableValueRange<float>(0f, 1f)));
        MimicChance = cfg.Bind(mim, "MimicChance", 0.65f,
            new ConfigDescription("Chance an enemy actually speaks when its timer is up.", new AcceptableValueRange<float>(0f, 1f)));
        HearingRange = cfg.Bind(mim, "HearingRange", 35f,
            new ConfigDescription("An enemy only mimics when a living player (other than the voice's owner) is within this many meters.", new AcceptableValueRange<float>(5f, 200f)));
        MinOwnerDistance = cfg.Bind(mim, "MinOwnerDistance", 25f,
            new ConfigDescription("A living player's voice is not used by an enemy closer to them than this. Farther players are preferred.", new AcceptableValueRange<float>(0f, 200f)));
        AllowNearbyOwnerFallback = cfg.Bind(mim, "AllowNearbyOwnerFallback", false,
            "If every voice owner is close to the enemy, use one anyway (with low weight) instead of staying quiet.");
        DeadOwnerWeight = cfg.Bind(mim, "DeadOwnerWeight", 1.5f,
            new ConfigDescription("Selection weight for dead players' voices relative to a far-away living player (1.0).", new AcceptableValueRange<float>(0f, 10f)));
        PitchVariation = cfg.Bind(mim, "PitchVariation", 0.05f,
            new ConfigDescription("Random pitch/speed variation (+/-). Small values keep it believable.", new AcceptableValueRange<float>(0f, 0.5f)));
        MinVolume = cfg.Bind(mim, "MinVolume", 0.8f,
            new ConfigDescription("Playback volume is randomized between this and 1.", new AcceptableValueRange<float>(0f, 1f)));
        FragmentChance = cfg.Bind(mim, "FragmentChance", 0.25f,
            new ConfigDescription("Chance to play only a random piece of a clip instead of the whole thing.", new AcceptableValueRange<float>(0f, 1f)));
        RepeatChance = cfg.Bind(mim, "RepeatChance", 0.15f,
            new ConfigDescription("Chance an enemy follows up with another clip from the same player a few seconds later.", new AcceptableValueRange<float>(0f, 1f)));
        ExcludedEnemies = cfg.Bind(mim, "ExcludedEnemies", "Docile Locust Bees,Manticoil,Red Locust Bees",
            "Comma-separated enemy names that never mimic.");

        const string share = "AI Voice Sharing";
        ShareVoiceWithHostAi = cfg.Bind(share, "ShareVoiceWithHostAi", true,
            "Only matters if the host runs the separate LethalMimics AI add-on and has it switched on (you get an on-screen notice). " +
            "Then your voice chat - only what other players could already hear - is sent to the host's computer so enemies can " +
            "clone your voice and speak as you. Set false to opt out; the host then never receives or clones your voice.");
        AiStreamSampleRate = cfg.Bind(share, "StreamSampleRate", 16000,
            new ConfigDescription("Sample rate of voice sent to the host's AI.", new AcceptableValueList<int>(16000, 24000)));
    }

    /// <summary>Per-user data (saved clips). Outside the mod-manager profile so profile exports never include it.</summary>
    public static string DataDir =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LethalMimics");

    public bool IsExcluded(string enemyName)
    {
        if (string.IsNullOrEmpty(enemyName)) return false;
        string src = ExcludedEnemies.Value ?? "";
        if (excludedCache == null || excludedCacheSource != src)
        {
            excludedCache = new HashSet<string>(
                src.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0),
                StringComparer.OrdinalIgnoreCase);
            excludedCacheSource = src;
        }
        return excludedCache.Contains(enemyName.Trim());
    }
}
