using System;
using System.Collections.Generic;
using Dissonance;
using Dissonance.Audio.Capture;
using NAudio.Wave;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LethalMimics;

/// <summary>
/// Hold-to-record. Taps the post-processed microphone stream from Dissonance (the game's voice chat),
/// which keeps flowing while voice chat is muted, so we can mute normal transmission while recording.
/// </summary>
internal sealed class VoiceRecorder : MonoBehaviour, IMicrophoneSubscriber
{
    public static bool IsRecording { get; private set; }
    private static VoiceRecorder instance;

    private InputAction recordAction;
    private InputAction clearAction;
    private InputAction dismissAction;
    private string dismissHint;
    private DissonanceComms comms;

    // Written from Dissonance's preprocessing thread, read on the main thread.
    private readonly object gate = new();
    private readonly List<float> buffer = new();
    private int micRate;
    private int micChannels;
    private volatile bool capturing;
    private float captureLimitSeconds;

    private float recordStart;
    // Short messages (clip saved, ...) show briefly on top of a longer notice, which comes back afterwards.
    private string hudText;
    private float hudUntil;
    private string noticeText;
    private float noticeUntil;
    private GUIStyle hudStyle;

    private void Awake() => instance = this;

    /// <summary>Shows a short message in the on-screen indicator area (and the log).</summary>
    public static void Notify(string text, float seconds = 2.5f)
    {
        Plugin.Log.LogInfo(text);
        if (instance != null) instance.ShowMessage(text, seconds);
    }

    private void Start()
    {
        recordAction = CreateAction("LethalMimics.Record", Plugin.Cfg.RecordKey.Value);
        clearAction = CreateAction("LethalMimics.ClearClips", Plugin.Cfg.ClearClipsKey.Value);
        dismissAction = CreateAction("LethalMimics.DismissNotice", Plugin.Cfg.DismissNoticeKey.Value);
        if (dismissAction != null)
            dismissHint = InputControlPath.ToHumanReadableString(Plugin.Cfg.DismissNoticeKey.Value.Trim(),
                InputControlPath.HumanReadableStringOptions.OmitDevice);
    }

    private static InputAction CreateAction(string name, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var action = new InputAction(name, InputActionType.Button, path.Trim());
            action.Enable();
            return action;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Invalid key binding '{path}' for {name}: {e.Message}");
            return null;
        }
    }

    private void Update()
    {
        if (comms == null) TrySubscribe();

        if (recordAction != null)
        {
            if (!IsRecording && recordAction.WasPressedThisFrame() && CanRecord())
                Begin();
            else if (IsRecording && (!recordAction.IsPressed() || Time.time - recordStart >= Plugin.Cfg.MaxClipSeconds.Value))
                End();
        }

        if (dismissAction != null && dismissAction.WasPressedThisFrame() && !IsTypingOrInMenu())
        {
            hudUntil = 0f;
            noticeUntil = 0f;
        }

        if (clearAction != null && clearAction.WasPressedThisFrame() && !IsRecording && CanRecord())
        {
            MimicManager.Instance.ClearLocalClips();
            ShowMessage("Deleted all of your mimic clips");
        }
    }

    private void TrySubscribe()
    {
        var round = StartOfRound.Instance;
        if (round == null || round.voiceChatModule == null) return;
        comms = round.voiceChatModule;
        comms.SubscribeToRecordedAudio(this);
    }

    private bool CanRecord() =>
        comms != null && MimicManager.Instance != null && GameNetworkManager.Instance != null &&
        GameNetworkManager.Instance.localPlayerController != null && !IsTypingOrInMenu();

    private static bool IsTypingOrInMenu()
    {
        var player = GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null;
        if (player == null) return false;
        return player.isTypingChat || player.inTerminalMenu ||
               (player.quickMenuManager != null && player.quickMenuManager.isMenuOpen);
    }

    private void Begin()
    {
        lock (gate) buffer.Clear();
        captureLimitSeconds = Plugin.Cfg.MaxClipSeconds.Value + 0.5f;
        capturing = true;
        IsRecording = true;
        recordStart = Time.time;
        // Patches.StartOfRoundUpdate keeps this muted every frame until recording ends.
        comms.IsMuted = true;
    }

    private void End()
    {
        capturing = false;
        IsRecording = false;

        float[] raw;
        int rate, channels;
        lock (gate)
        {
            raw = buffer.ToArray();
            buffer.Clear();
            rate = micRate;
            channels = micChannels;
        }

        try
        {
            ShowMessage(ProcessAndSubmit(raw, rate, channels));
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Failed to process recording: {e}");
            ShowMessage("Recording failed (see log)");
        }
    }

    private static string ProcessAndSubmit(float[] raw, int rate, int channels)
    {
        var cfg = Plugin.Cfg;
        if (raw.Length == 0 || rate <= 0) return "No microphone audio captured - is your mic enabled?";

        float[] mono = AudioCodec.Downmix(raw, Math.Max(1, channels));
        if (cfg.TrimSilence.Value) mono = AudioCodec.TrimSilence(mono, rate);

        float duration = (float)mono.Length / rate;
        if (duration < cfg.MinClipSeconds.Value) return "Clip too short or silent - not saved";

        int outRate = cfg.TransmitSampleRate.Value;
        float[] samples = AudioCodec.Resample(mono, rate, outRate);
        AudioCodec.Normalize(samples);
        AudioCodec.ApplyFades(samples, outRate / 200);
        byte[] encoded = AudioCodec.EncodeMuLaw(samples);

        string path = cfg.SaveClips.Value ? ClipPersistence.Save(outRate, encoded) : null;
        MimicManager.Instance.SubmitLocalClip(outRate, encoded, path);
        return $"Mimic clip saved ({duration:0.0}s) - {MimicManager.Instance.OwnClipCount} stored";
    }

    void IMicrophoneSubscriber.ReceiveMicrophoneData(ArraySegment<float> data, WaveFormat format)
    {
        if (!capturing) return;
        lock (gate)
        {
            micRate = format.SampleRate;
            micChannels = format.Channels;
            int limit = (int)(captureLimitSeconds * micRate * Math.Max(1, micChannels));
            int n = Math.Min(data.Count, limit - buffer.Count);
            for (int i = 0; i < n; i++) buffer.Add(data.Array[data.Offset + i]);
        }
    }

    void IMicrophoneSubscriber.Reset() { }

    private void ShowMessage(string text, float seconds = 2.5f)
    {
        if (seconds > 4f)
        {
            noticeText = text;
            noticeUntil = Time.time + seconds;
            hudUntil = 0f;
        }
        else
        {
            hudText = text;
            hudUntil = Time.time + seconds;
        }
    }

    private void OnGUI()
    {
        if (!Plugin.Cfg.ShowHud.Value) return;

        string text;
        Color color;
        if (IsRecording)
        {
            text = $"● REC mimic voice   {Time.time - recordStart:0.0}s / {Plugin.Cfg.MaxClipSeconds.Value:0}s";
            color = new Color(1f, 0.25f, 0.2f);
        }
        else if (Time.time < hudUntil)
        {
            text = hudText;
            color = new Color(1f, 0.85f, 0.4f);
        }
        else if (Time.time < noticeUntil)
        {
            text = dismissHint != null ? $"{noticeText}\n<size=15>[{dismissHint}] dismiss</size>" : noticeText;
            color = new Color(1f, 0.85f, 0.4f);
        }
        else return;

        hudStyle ??= new GUIStyle(GUI.skin.label)
        {
            fontSize = 20,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
        };

        hudStyle.wordWrap = true;
        hudStyle.richText = true;
        var rect = new Rect(Screen.width / 2f - 400f, Screen.height * 0.76f, 800f, 90f);
        hudStyle.normal.textColor = new Color(0f, 0f, 0f, 0.8f);
        GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height), text, hudStyle);
        hudStyle.normal.textColor = color;
        GUI.Label(rect, text, hudStyle);
    }

    private void OnDestroy()
    {
        if (instance == this) instance = null;
        capturing = false;
        IsRecording = false;
        if (comms != null)
        {
            try { comms.UnsubscribeFromRecordedAudio(this); }
            catch (Exception e) { Plugin.Log.LogDebug($"Unsubscribe failed: {e.Message}"); }
        }
        recordAction?.Dispose();
        clearAction?.Dispose();
        dismissAction?.Dispose();
    }
}
