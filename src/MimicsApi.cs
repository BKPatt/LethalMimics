using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;

namespace LethalMimics;

/// <summary>
/// A host-side voice AI provided by another plugin (the separate LethalMimics AI add-on). LethalMimics itself
/// contains no AI: it only forwards speech to a registered provider and plays what the provider generates.
/// All calls happen on the host, on the main thread.
/// </summary>
public interface IMimicVoiceAi
{
    /// <summary>The AI is switched on for this lobby. Players are told, and only then send their voice.</summary>
    bool Active { get; }

    /// <summary>Chance to use an AI line instead of a recorded clip when a player has both.</summary>
    float LineChance { get; }

    /// <summary>A stretch of a consenting player's live voice chat.</summary>
    void OnSpeech(ulong clientId, float[] samples, int sampleRate);

    /// <summary>A consenting player's recorded mimic clip.</summary>
    void OnRecordedClip(ulong clientId, float[] samples, int sampleRate);

    void OnPlayerLeft(ulong clientId);

    /// <summary>Players the AI has a ready-to-play line for.</summary>
    IEnumerable<ulong> OwnersWithLines();

    bool TryTakeLine(ulong owner, out float[] samples, out int sampleRate);
}

/// <summary>A mimic an enemy recently played: which enemy, whose voice, and when (Time.time).</summary>
public readonly struct MimicEvent
{
    public readonly EnemyAI Enemy;
    public readonly ulong Owner;
    public readonly float Time;

    public MimicEvent(EnemyAI enemy, ulong owner, float time)
    {
        Enemy = enemy;
        Owner = owner;
        Time = time;
    }
}

/// <summary>Entry points for add-ons. Everything here is host-side unless noted.</summary>
public static class MimicsApi
{
    /// <summary>The registered voice AI, if any.</summary>
    public static IMimicVoiceAi VoiceAi { get; private set; }

    /// <summary>
    /// Raised on the host when a lobby session starts, with the session object (it is destroyed when the lobby ends).
    /// Add-ons attach their per-lobby components to it.
    /// </summary>
    public static event Action<GameObject> HostSessionStarted;

    public static void RegisterVoiceAi(IMimicVoiceAi ai) => VoiceAi = ai;

    public static void UnregisterVoiceAi(IMimicVoiceAi ai)
    {
        if (VoiceAi == ai) VoiceAi = null;
    }

    /// <summary>Call when <see cref="IMimicVoiceAi.Active"/> changes so players are told and start/stop sending their voice.</summary>
    public static void VoiceAiStateChanged()
    {
        var manager = MimicManager.Instance;
        if (manager != null) manager.BroadcastAiState();
    }

    /// <summary>Whether this player's voice may be given to the AI (AI on and the player hasn't opted out).</summary>
    public static bool VoiceAllowedFor(ulong clientId)
    {
        var manager = MimicManager.Instance;
        return manager != null && manager.AiAllowedFor(clientId);
    }

    /// <summary>Plays generated speech in <paramref name="owner"/>'s voice from <paramref name="enemy"/> for everyone but the owner.</summary>
    public static void PlayGenerated(EnemyAI enemy, ulong owner, float[] samples, int sampleRate)
    {
        var manager = MimicManager.Instance;
        if (manager != null) manager.HostPlayGenerated(enemy, owner, samples, sampleRate);
    }

    /// <summary>Mimics played in the last <paramref name="withinSeconds"/>, newest first.</summary>
    public static IEnumerable<MimicEvent> RecentMimics(float withinSeconds)
    {
        var manager = MimicManager.Instance;
        return manager != null ? manager.RecentMimics(withinSeconds) : Array.Empty<MimicEvent>();
    }

    /// <summary>Keeps the scheduler from using <paramref name="enemy"/> for this long (0 = release).</summary>
    public static void MarkBusy(EnemyAI enemy, float seconds)
    {
        var manager = MimicManager.Instance;
        if (manager != null) manager.MarkBusy(enemy, seconds);
    }

    /// <summary>The closest mimic-capable enemy to <paramref name="listener"/> that could plausibly be <paramref name="owner"/>.</summary>
    public static EnemyAI EnemyThatCouldBe(ulong owner, PlayerControllerB listener)
    {
        var manager = MimicManager.Instance;
        return manager != null ? manager.EnemyThatCouldBe(owner, listener) : null;
    }

    public static PlayerControllerB FindPlayer(ulong clientId) => MimicManager.FindPlayer(clientId);

    /// <summary>Meters within which a player can hear a mimic (host's setting).</summary>
    public static float HearingRange => Plugin.Cfg.HearingRange.Value;

    /// <summary>Shows a notice on this player's screen (works on any player).</summary>
    public static void Notify(string text, float seconds = 4f) => VoiceRecorder.Notify(text, seconds);

    internal static void RaiseHostSessionStarted(GameObject session)
    {
        try
        {
            HostSessionStarted?.Invoke(session);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"An add-on failed to start: {e}");
        }
    }
}
