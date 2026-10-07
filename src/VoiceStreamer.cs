using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Dissonance;
using Dissonance.Audio.Capture;
using NAudio.Wave;
using UnityEngine;

namespace LethalMimics;

/// <summary>
/// Runs on every player. While the host has AI cloning on (and this player hasn't opted out), cuts this
/// player's outgoing voice chat into utterances with a simple energy VAD and hands them to MimicManager,
/// which sends them to the host. Only audio that is actually being transmitted is used: nothing while
/// push-to-talk is released, the mic is disabled, or a mimic clip is being recorded.
/// </summary>
internal sealed class VoiceStreamer : MonoBehaviour, IMicrophoneSubscriber
{
    private const float FrameSeconds = 0.02f;
    private const int StartFrames = 3;          // 60ms of speech opens an utterance
    private const int EndSilenceFrames = 35;    // 700ms of quiet closes it
    private const int PrerollFrames = 10;       // keep 200ms before the trigger
    private const float MaxUtteranceSeconds = 15f;
    private const float MinUtteranceSeconds = 0.8f;

    private DissonanceComms comms;
    private volatile bool open;
    private volatile int micRate;
    private volatile int micChannels;
    private readonly ConcurrentQueue<float[]> incoming = new();

    private readonly List<float> pendingSamples = new();
    private readonly Queue<float[]> preroll = new();
    private readonly List<float> utterance = new();
    private bool inSpeech;
    private int speechRun;
    private int silenceRun;
    private float noiseFloorDb = -60f;

    private void Update()
    {
        if (comms == null)
        {
            var round = StartOfRound.Instance;
            if (round == null || round.voiceChatModule == null) return;
            comms = round.voiceChatModule;
            comms.SubscribeToRecordedAudio(this);
        }

        var manager = MimicManager.Instance;
        bool shouldOpen = manager != null && manager.AiActive && Plugin.Cfg.ShareVoiceWithHostAi.Value &&
                          !comms.IsMuted && !VoiceRecorder.IsRecording &&
                          IngamePlayerSettings.Instance != null && IngamePlayerSettings.Instance.settings.micEnabled;

        if (!shouldOpen && open)
        {
            open = false;
            FinishUtterance();
            ResetVad();
        }
        open = shouldOpen;

        while (incoming.TryDequeue(out var block))
        {
            if (open) ProcessBlock(block);
        }
    }

    void IMicrophoneSubscriber.ReceiveMicrophoneData(ArraySegment<float> data, WaveFormat format)
    {
        if (!open) return;
        micRate = format.SampleRate;
        micChannels = format.Channels;
        var copy = new float[data.Count];
        Array.Copy(data.Array, data.Offset, copy, 0, data.Count);
        incoming.Enqueue(copy);
    }

    void IMicrophoneSubscriber.Reset() { }

    private void ProcessBlock(float[] block)
    {
        int rate = micRate;
        if (rate <= 0) return;
        float[] mono = AudioCodec.Downmix(block, Math.Max(1, micChannels));
        pendingSamples.AddRange(mono);

        int frameLen = Mathf.Max(1, (int)(rate * FrameSeconds));
        int consumed = 0;
        while (pendingSamples.Count - consumed >= frameLen)
        {
            var frame = pendingSamples.GetRange(consumed, frameLen).ToArray();
            consumed += frameLen;
            ProcessFrame(frame, rate);
        }
        pendingSamples.RemoveRange(0, consumed);
    }

    private void ProcessFrame(float[] frame, int rate)
    {
        double sum = 0;
        foreach (float s in frame) sum += s * s;
        float db = 10f * Mathf.Log10((float)(sum / frame.Length) + 1e-12f);

        // Adaptive noise floor: falls quickly, rises slowly.
        noiseFloorDb = db < noiseFloorDb ? Mathf.Lerp(noiseFloorDb, db, 0.3f) : noiseFloorDb + 0.01f;
        bool speech = db > Mathf.Max(-52f, noiseFloorDb + 10f);

        if (!inSpeech)
        {
            preroll.Enqueue(frame);
            while (preroll.Count > PrerollFrames) preroll.Dequeue();
            speechRun = speech ? speechRun + 1 : 0;
            if (speechRun < StartFrames) return;

            inSpeech = true;
            silenceRun = 0;
            utterance.Clear();
            foreach (var f in preroll) utterance.AddRange(f);
            preroll.Clear();
            return;
        }

        utterance.AddRange(frame);
        silenceRun = speech ? 0 : silenceRun + 1;
        if (silenceRun >= EndSilenceFrames || utterance.Count >= MaxUtteranceSeconds * rate)
        {
            FinishUtterance();
            ResetVad();
        }
    }

    private void FinishUtterance()
    {
        int rate = micRate;
        if (!inSpeech || rate <= 0)
        {
            utterance.Clear();
            return;
        }

        // Drop most of the trailing silence that ended the utterance.
        int trailing = Mathf.Min(utterance.Count, Mathf.Max(0, silenceRun - 10) * (int)(rate * FrameSeconds));
        int keep = utterance.Count - trailing;
        if (keep >= MinUtteranceSeconds * rate)
        {
            var samples = utterance.GetRange(0, keep).ToArray();
            int outRate = Plugin.Cfg.AiStreamSampleRate.Value;
            var manager = MimicManager.Instance;
            if (manager != null) manager.SubmitUtterance(AudioCodec.Resample(samples, rate, outRate), outRate);
        }
        utterance.Clear();
    }

    private void ResetVad()
    {
        inSpeech = false;
        speechRun = 0;
        silenceRun = 0;
        preroll.Clear();
    }

    private void OnDestroy()
    {
        open = false;
        if (comms == null) return;
        try { comms.UnsubscribeFromRecordedAudio(this); }
        catch (Exception e) { Plugin.Log.LogDebug($"Unsubscribe failed: {e.Message}"); }
    }
}
