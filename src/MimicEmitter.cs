using GameNetcodeStuff;
using UnityEngine;

namespace LethalMimics;

/// <summary>Lives on an enemy and plays mimic clips from its head, routed like a real player voice.</summary>
internal sealed class MimicEmitter : MonoBehaviour
{
    private AudioSource source;
    private AudioClip current;

    public void Play(MimicClip clip, int start, int length, float pitch, float volume, PlayerControllerB owner)
    {
        EnsureSource();
        StopCurrent();

        float[] samples = AudioCodec.DecodeMuLaw(clip.Encoded, start, length);
        AudioCodec.ApplyFades(samples, clip.SampleRate / 100);
        current = AudioClip.Create("LethalMimicsClip", samples.Length, 1, clip.SampleRate, false);
        current.SetData(samples, 0);

        MatchOwnerVoice(owner);
        source.clip = current;
        source.pitch = pitch;
        source.volume = volume;
        source.Play();
    }

    private void EnsureSource()
    {
        if (source != null) return;

        var enemy = GetComponent<EnemyAI>();
        Transform anchor = enemy != null && enemy.eye != null ? enemy.eye : transform;

        var go = new GameObject("LethalMimicsVoice");
        go.transform.SetParent(anchor, false);

        source = go.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = false;
        source.spatialBlend = 1f;
        source.dopplerLevel = 0f;
        source.rolloffMode = AudioRolloffMode.Linear;
        source.minDistance = 2f;
        source.maxDistance = 45f;

        // Same muffling-through-walls behaviour the game gives player voices.
        go.AddComponent<AudioLowPassFilter>().cutoffFrequency = 20000f;
        go.AddComponent<OccludeAudio>();
    }

    private void MatchOwnerVoice(PlayerControllerB owner)
    {
        if (owner == null) return;

        // The owner's voice mixer carries their per-player volume slider and pitch effects.
        var sound = SoundManager.Instance;
        if (sound != null && sound.playerVoiceMixers != null && owner.playerClientId < (ulong)sound.playerVoiceMixers.Length)
            source.outputAudioMixerGroup = sound.playerVoiceMixers[owner.playerClientId];

        var template = owner.currentVoiceChatAudioSource;
        if (template == null) return;
        source.rolloffMode = template.rolloffMode;
        source.minDistance = template.minDistance;
        source.maxDistance = template.maxDistance;
        if (template.rolloffMode == AudioRolloffMode.Custom)
            source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, template.GetCustomCurve(AudioSourceCurveType.CustomRolloff));
    }

    private void Update()
    {
        if (current != null && source != null && !source.isPlaying) StopCurrent();
    }

    private void StopCurrent()
    {
        if (source != null)
        {
            source.Stop();
            source.clip = null;
        }
        if (current != null)
        {
            Destroy(current);
            current = null;
        }
    }

    private void OnDestroy()
    {
        if (current != null) Destroy(current);
    }
}
