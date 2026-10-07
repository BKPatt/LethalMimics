using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LethalMimics;

/// <summary>
/// Session-scoped (attached to StartOfRound). Syncs clips between players and, on the host,
/// decides when enemies mimic and tells every client to play the same clip at the same time.
///
/// Flow: recorder -> owner sends clip to host -> host stores and relays to everyone except the owner.
/// Host scheduler -> "Play" message to everyone except the owner -> each client plays it on that enemy.
/// With a voice AI add-on active on the host (see <see cref="MimicsApi"/>): consenting players' speech -> host -> add-on;
/// AI lines are sent like clips but are one-shot ("generated") and never enter the recorded-clip pool.
/// </summary>
internal sealed class MimicManager : MonoBehaviour
{
    public static MimicManager Instance { get; private set; }

    private const string MsgClip = "LethalMimics.Clip";
    private const string MsgVoice = "LethalMimics.Voice";
    private const string MsgRemove = "LethalMimics.Remove";
    private const string MsgSync = "LethalMimics.SyncRequest";
    private const string MsgPlay = "LethalMimics.Play";
    private const string MsgAiState = "LethalMimics.AiState";
    private const int ChunkSize = 16 * 1024;
    private const int MaxClipBytes = 48000 * 30;
    private const uint AllClips = uint.MaxValue;
    private const uint GeneratedIdBit = 0x80000000;

    private enum Payload : byte { Clip, Voice }

    private readonly ClipStore store = new();
    private readonly Dictionary<(Payload kind, ulong owner, uint id), PendingTransfer> pending = new();
    private readonly List<OwnClip> ownClips = new();
    private readonly List<PendingPlay> pendingPlays = new();
    private uint nextLocalId = 1;
    private uint nextVoiceId = 1;
    private uint nextGeneratedId = GeneratedIdBit;
    private bool registered;

    // Voice AI add-on state
    private readonly HashSet<ulong> aiOptOut = new();
    private bool clientAiActive;
    private bool lastBroadcastAiState;

    // Host scheduler state
    private readonly Dictionary<EnemyAI, float> nextMimicTime = new();
    private readonly Dictionary<EnemyAI, float> busyUntil = new();
    private readonly List<(ulong owner, uint id)> recentlyPlayed = new();
    private readonly List<MimicEvent> mimicEvents = new();
    private readonly Dictionary<EnemyAI, bool> isMimic = new();
    private readonly Dictionary<ulong, float> listenerReadyTime = new();
    private float levelMimicShare = -1f;
    private float nextEvaluation;

    private sealed class PendingTransfer
    {
        public int Total, Count, SampleRate, Received;
        public byte[] Data;
        public bool[] Got;
        public float Started;
    }

    private readonly struct OwnClip
    {
        public readonly uint Id;
        public readonly string Path;

        public OwnClip(uint id, string path)
        {
            Id = id;
            Path = path;
        }
    }

    private sealed class PendingPlay
    {
        public ulong EnemyId, Owner;
        public uint ClipId;
        public float Pitch, Volume, Received;
        public int Start, Length;
    }

    private enum MimicResult { Played, ChanceFailed, NoOpportunity }

    private static NetworkManager Net => NetworkManager.Singleton;
    private static bool IsServer => Net != null && Net.IsServer;
    private static ulong LocalId => Net.LocalClientId;
    private static MimicConfig Cfg => Plugin.Cfg;

    public int OwnClipCount => ownClips.Count;

    /// <summary>True when the host has AI cloning running, so players should stream their speech.</summary>
    public bool AiActive => IsServer ? AiRunning : clientAiActive;

    /// <summary>Host: a voice AI add-on is registered and switched on.</summary>
    private static bool AiRunning => Ai != null && Ai.Active;

    private static IMimicVoiceAi Ai => MimicsApi.VoiceAi;

    private void Awake() => Instance = this;

    private void Start()
    {
        var messaging = Net != null ? Net.CustomMessagingManager : null;
        if (messaging == null)
        {
            Plugin.Log.LogError("Netcode messaging unavailable; mimic voices disabled for this session.");
            return;
        }

        messaging.RegisterNamedMessageHandler(MsgClip, (s, r) => OnTransferMessage(Payload.Clip, s, r));
        messaging.RegisterNamedMessageHandler(MsgVoice, (s, r) => OnTransferMessage(Payload.Voice, s, r));
        messaging.RegisterNamedMessageHandler(MsgRemove, OnRemoveMessage);
        messaging.RegisterNamedMessageHandler(MsgSync, OnSyncRequest);
        messaging.RegisterNamedMessageHandler(MsgPlay, OnPlayMessage);
        messaging.RegisterNamedMessageHandler(MsgAiState, OnAiStateMessage);
        registered = true;

        if (IsServer)
        {
            Net.OnClientDisconnectCallback += OnClientDisconnected;
            if (!Cfg.ShareVoiceWithHostAi.Value) aiOptOut.Add(LocalId);
            MimicsApi.RaiseHostSessionStarted(gameObject);
            lastBroadcastAiState = AiRunning;
        }
        else
        {
            SendSyncRequest();
        }

        if (Cfg.SaveClips.Value)
        {
            var saved = ClipPersistence.LoadAll(Cfg.MaxSavedClips.Value);
            foreach (var clip in saved) SubmitLocalClip(clip.SampleRate, clip.Encoded, clip.Path);
            if (saved.Count > 0) Plugin.Log.LogInfo($"Uploaded {saved.Count} saved mimic clip(s).");
        }
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (Net == null) return;
        if (registered && Net.CustomMessagingManager != null)
        {
            foreach (string name in new[] { MsgClip, MsgVoice, MsgRemove, MsgSync, MsgPlay, MsgAiState })
                Net.CustomMessagingManager.UnregisterNamedMessageHandler(name);
        }
        Net.OnClientDisconnectCallback -= OnClientDisconnected;
    }

    // ---------------------------------------------------------------- local player's clips

    public void SubmitLocalClip(int sampleRate, byte[] encoded, string savedPath)
    {
        if (!registered) return;

        uint id = nextLocalId++;
        ownClips.Add(new OwnClip(id, savedPath));
        while (ownClips.Count > Cfg.MaxSavedClips.Value)
        {
            var oldest = ownClips[0];
            ownClips.RemoveAt(0);
            ClipPersistence.Delete(oldest.Path);
            RemoveOwnClip(oldest.Id);
        }

        var clip = new MimicClip(LocalId, id, sampleRate, encoded);
        if (IsServer)
            HostAddClip(clip);
        else
            SendTransfer(MsgClip, clip.Owner, clip.Id, clip.SampleRate, clip.Encoded, new[] { NetworkManager.ServerClientId });
    }

    public void ClearLocalClips()
    {
        foreach (var clip in ownClips) ClipPersistence.Delete(clip.Path);
        ownClips.Clear();
        RemoveOwnClip(AllClips);
    }

    private void RemoveOwnClip(uint id)
    {
        if (IsServer)
        {
            HostRemove(LocalId, id, except: LocalId);
            return;
        }
        using var w = new FastBufferWriter(16, Allocator.Temp);
        w.WriteValueSafe(LocalId);
        w.WriteValueSafe(id);
        Net.CustomMessagingManager.SendNamedMessage(MsgRemove, NetworkManager.ServerClientId, w, NetworkDelivery.ReliableSequenced);
    }

    /// <summary>An utterance from this player's live voice chat (VoiceStreamer), for the host's AI.</summary>
    public void SubmitUtterance(float[] samples, int sampleRate)
    {
        if (!registered || !AiActive || !Cfg.ShareVoiceWithHostAi.Value || samples.Length == 0) return;

        if (IsServer)
        {
            Ai.OnSpeech(LocalId, samples, sampleRate);
            return;
        }

        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short v = (short)Mathf.Clamp(samples[i] * 32767f, -32768f, 32767f);
            bytes[i * 2] = (byte)v;
            bytes[i * 2 + 1] = (byte)(v >> 8);
        }
        SendTransfer(MsgVoice, LocalId, nextVoiceId++, sampleRate, bytes, new[] { NetworkManager.ServerClientId });
    }

    // ---------------------------------------------------------------- chunked transfer (clips and voice)

    private void SendTransfer(string message, ulong owner, uint id, int sampleRate, byte[] data, IReadOnlyList<ulong> targets)
    {
        if (targets.Count == 0) return;
        int total = data.Length;
        int count = Mathf.Max(1, (total + ChunkSize - 1) / ChunkSize);

        for (int i = 0; i < count; i++)
        {
            int offset = i * ChunkSize;
            int len = Mathf.Min(ChunkSize, total - offset);
            using var w = new FastBufferWriter(ChunkSize + 64, Allocator.Temp);
            w.WriteValueSafe(owner);
            w.WriteValueSafe(id);
            w.WriteValueSafe(sampleRate);
            w.WriteValueSafe(total);
            w.WriteValueSafe(i);
            w.WriteValueSafe(count);
            w.WriteValueSafe(len);
            w.WriteBytesSafe(data, len, offset);

            if (IsServer)
                Net.CustomMessagingManager.SendNamedMessage(message, targets, w, NetworkDelivery.ReliableFragmentedSequenced);
            else
                Net.CustomMessagingManager.SendNamedMessage(message, targets[0], w, NetworkDelivery.ReliableFragmentedSequenced);
        }
    }

    private void OnTransferMessage(Payload kind, ulong sender, FastBufferReader reader)
    {
        reader.ReadValueSafe(out ulong owner);
        reader.ReadValueSafe(out uint id);
        reader.ReadValueSafe(out int rate);
        reader.ReadValueSafe(out int total);
        reader.ReadValueSafe(out int index);
        reader.ReadValueSafe(out int count);
        reader.ReadValueSafe(out int len);

        if (IsServer)
        {
            if (sender == NetworkManager.ServerClientId) return;
            owner = sender; // clients may only upload their own voice
            if (kind == Payload.Clip && (id & GeneratedIdBit) != 0) return;
            if (kind == Payload.Voice && !AiAllowedFor(sender)) return;
        }
        else if (kind == Payload.Voice || sender != NetworkManager.ServerClientId || owner == LocalId)
        {
            return;
        }

        int maxBytes = kind == Payload.Voice ? 48000 * 2 * 16 : MaxClipBytes;
        int expectedCount = (total + ChunkSize - 1) / ChunkSize;
        bool valid = rate >= 4000 && rate <= 48000 && total > 0 && total <= maxBytes &&
                     count == expectedCount && index >= 0 && index < count &&
                     len > 0 && len <= ChunkSize && index * ChunkSize + len <= total;
        if (!valid)
        {
            Plugin.Log.LogWarning($"Dropping malformed {kind} chunk from client {sender}.");
            return;
        }
        if (kind == Payload.Clip && store.Get(owner, id) != null) return;

        var key = (kind, owner, id);
        if (!pending.TryGetValue(key, out var p) || p.Total != total || p.SampleRate != rate)
        {
            p = new PendingTransfer
            {
                Total = total, Count = count, SampleRate = rate,
                Data = new byte[total], Got = new bool[count], Started = Time.time,
            };
            pending[key] = p;
        }

        reader.ReadBytesSafe(ref p.Data, len, index * ChunkSize);
        if (!p.Got[index])
        {
            p.Got[index] = true;
            p.Received++;
        }
        if (p.Received < p.Count) return;

        pending.Remove(key);
        if (kind == Payload.Voice)
        {
            var samples = new float[p.Data.Length / 2];
            for (int i = 0; i < samples.Length; i++) samples[i] = (short)(p.Data[i * 2] | (p.Data[i * 2 + 1] << 8)) / 32768f;
            if (AiAllowedFor(owner)) Ai.OnSpeech(owner, samples, rate);
            return;
        }

        var clip = new MimicClip(owner, id, rate, p.Data);
        if (IsServer)
        {
            HostAddClip(clip);
        }
        else
        {
            store.Add(clip);
            FlushPendingPlays(owner, id);
        }
    }

    private void HostAddClip(MimicClip clip)
    {
        var evicted = store.Add(clip, Cfg.MaxClipsPerPlayer.Value);
        SendTransfer(MsgClip, clip.Owner, clip.Id, clip.SampleRate, clip.Encoded, ClientsExcept(clip.Owner));
        foreach (var old in evicted) HostRemove(old.Owner, old.Id, except: null);
        if (AiAllowedFor(clip.Owner)) Ai.OnRecordedClip(clip.Owner, AudioCodec.DecodeMuLaw(clip.Encoded, 0, clip.SampleCount), clip.SampleRate);
        Plugin.Log.LogDebug($"Stored clip {clip.Id} from client {clip.Owner} ({clip.Duration:0.0}s). Total clips: {store.Count}");
    }

    // ---------------------------------------------------------------- removal / sync / AI state / disconnect

    private void OnRemoveMessage(ulong sender, FastBufferReader reader)
    {
        reader.ReadValueSafe(out ulong owner);
        reader.ReadValueSafe(out uint id);

        if (IsServer)
        {
            if (sender != NetworkManager.ServerClientId) HostRemove(sender, id, except: sender);
        }
        else if (sender == NetworkManager.ServerClientId)
        {
            RemoveLocal(owner, id);
        }
    }

    private void HostRemove(ulong owner, uint id, ulong? except)
    {
        RemoveLocal(owner, id);
        var targets = except.HasValue ? ClientsExcept(except.Value) : ClientsExcept();
        if (targets.Count == 0) return;
        using var w = new FastBufferWriter(16, Allocator.Temp);
        w.WriteValueSafe(owner);
        w.WriteValueSafe(id);
        Net.CustomMessagingManager.SendNamedMessage(MsgRemove, targets, w, NetworkDelivery.ReliableSequenced);
    }

    private void RemoveLocal(ulong owner, uint id)
    {
        if (id == AllClips)
        {
            store.RemoveAll(owner);
            foreach (var key in pending.Keys.Where(k => k.owner == owner).ToList()) pending.Remove(key);
        }
        else
        {
            store.Remove(owner, id);
        }
    }

    private void SendSyncRequest()
    {
        using var w = new FastBufferWriter(4, Allocator.Temp);
        w.WriteValueSafe((byte)2); // protocol version
        w.WriteValueSafe(Cfg.ShareVoiceWithHostAi.Value);
        Net.CustomMessagingManager.SendNamedMessage(MsgSync, NetworkManager.ServerClientId, w, NetworkDelivery.ReliableSequenced);
    }

    private void OnSyncRequest(ulong sender, FastBufferReader reader)
    {
        if (!IsServer || sender == NetworkManager.ServerClientId) return;

        // Old versions of the mod send no consent flag: treat them as opted out of AI cloning.
        bool shares = false;
        if (reader.TryBeginRead(2))
        {
            reader.ReadValue(out byte _);
            reader.ReadValue(out shares);
        }
        if (shares) aiOptOut.Remove(sender);
        else aiOptOut.Add(sender);

        using (var w = new FastBufferWriter(4, Allocator.Temp))
        {
            w.WriteValueSafe(AiRunning);
            Net.CustomMessagingManager.SendNamedMessage(MsgAiState, sender, w, NetworkDelivery.ReliableSequenced);
        }

        var clips = store.All.Where(c => c.Owner != sender).ToList();
        foreach (var clip in clips) SendTransfer(MsgClip, clip.Owner, clip.Id, clip.SampleRate, clip.Encoded, new[] { sender });
        Plugin.Log.LogDebug($"Sent {clips.Count} clip(s) to joining client {sender} (AI sharing: {shares}).");
    }

    private void OnAiStateMessage(ulong sender, FastBufferReader reader)
    {
        if (IsServer || sender != NetworkManager.ServerClientId) return;
        reader.ReadValueSafe(out bool active);
        bool wasActive = clientAiActive;
        clientAiActive = active;
        if (!active)
        {
            if (wasActive) VoiceRecorder.Notify("Host turned AI voice cloning OFF - your voice is no longer being sent.", 6f);
            return;
        }
        if (wasActive) return;
        VoiceRecorder.Notify(Cfg.ShareVoiceWithHostAi.Value
            ? "Host has AI voice cloning ON: your voice chat is sent to the host so enemies can speak as you. Set ShareVoiceWithHostAi=false to opt out."
            : "Host has AI voice cloning ON. You opted out, so your voice is not shared or cloned.", 25f);
    }

    /// <summary>Host: tell everyone whether the voice AI add-on is on (they only send their voice while it is).</summary>
    internal void BroadcastAiState()
    {
        if (!IsServer || !registered) return;
        bool on = AiRunning;
        if (on == lastBroadcastAiState) return;
        lastBroadcastAiState = on;

        var targets = ClientsExcept();
        if (targets.Count == 0) return;
        using var w = new FastBufferWriter(4, Allocator.Temp);
        w.WriteValueSafe(on);
        Net.CustomMessagingManager.SendNamedMessage(MsgAiState, targets, w, NetworkDelivery.ReliableSequenced);
    }

    public bool AiAllowedFor(ulong clientId) => AiRunning && !aiOptOut.Contains(clientId);

    private void OnClientDisconnected(ulong clientId)
    {
        if (!IsServer || clientId == NetworkManager.ServerClientId) return;
        HostRemove(clientId, AllClips, except: clientId);
        aiOptOut.Remove(clientId);
        Ai?.OnPlayerLeft(clientId);
    }

    private static List<ulong> ClientsExcept(params ulong[] excluded)
    {
        var result = new List<ulong>();
        foreach (ulong id in Net.ConnectedClientsIds)
        {
            if (id == NetworkManager.ServerClientId || excluded.Contains(id)) continue;
            result.Add(id);
        }
        return result;
    }

    // ---------------------------------------------------------------- playback

    private void OnPlayMessage(ulong sender, FastBufferReader reader)
    {
        if (IsServer || sender != NetworkManager.ServerClientId) return;
        var play = new PendingPlay { Received = Time.time };
        reader.ReadValueSafe(out play.EnemyId);
        reader.ReadValueSafe(out play.Owner);
        reader.ReadValueSafe(out play.ClipId);
        reader.ReadValueSafe(out play.Pitch);
        reader.ReadValueSafe(out play.Volume);
        reader.ReadValueSafe(out play.Start);
        reader.ReadValueSafe(out play.Length);

        if (play.Owner == LocalId) return; // you never hear your own mimic
        if (!TryPlay(play)) pendingPlays.Add(play); // clip may still be arriving
    }

    private bool TryPlay(PendingPlay play)
    {
        var clip = store.Get(play.Owner, play.ClipId);
        if (clip == null) return false;
        if (Net.SpawnManager.SpawnedObjects.TryGetValue(play.EnemyId, out var netObj) && netObj != null)
            PlayLocal(netObj.GetComponentInChildren<EnemyAI>(), clip, play.Pitch, play.Volume, play.Start, play.Length);
        return true;
    }

    private void FlushPendingPlays(ulong owner, uint id)
    {
        for (int i = pendingPlays.Count - 1; i >= 0; i--)
        {
            var play = pendingPlays[i];
            if (play.Owner != owner || play.ClipId != id) continue;
            pendingPlays.RemoveAt(i);
            TryPlay(play);
        }
    }

    private void HostBroadcastPlay(EnemyAI enemy, MimicClip clip, float pitch, float volume, int start, int length)
    {
        float quietUntil = Time.time + length / (float)clip.SampleRate / Mathf.Max(pitch, 0.1f) + Cfg.ListenerCooldown.Value;
        foreach (var p in ListenersOf(enemy)) listenerReadyTime[p.actualClientId] = quietUntil;

        var targets = ClientsExcept(clip.Owner);
        if (targets.Count > 0)
        {
            using var w = new FastBufferWriter(64, Allocator.Temp);
            w.WriteValueSafe(enemy.NetworkObjectId);
            w.WriteValueSafe(clip.Owner);
            w.WriteValueSafe(clip.Id);
            w.WriteValueSafe(pitch);
            w.WriteValueSafe(volume);
            w.WriteValueSafe(start);
            w.WriteValueSafe(length);
            // Same channel as clip chunks so a freshly generated clip arrives before its play order.
            Net.CustomMessagingManager.SendNamedMessage(MsgPlay, targets, w, NetworkDelivery.ReliableFragmentedSequenced);
        }
        if (clip.Owner != LocalId) PlayLocal(enemy, clip, pitch, volume, start, length);

        busyUntil[enemy] = Time.time + length / (float)clip.SampleRate / Mathf.Max(0.1f, pitch) + 0.5f;
        mimicEvents.Add(new MimicEvent(enemy, clip.Owner, Time.time));
        if ((clip.Id & GeneratedIdBit) == 0)
        {
            recentlyPlayed.Add((clip.Owner, clip.Id));
            if (recentlyPlayed.Count > 6) recentlyPlayed.RemoveAt(0);
        }
    }

    /// <summary>Host: play freshly generated AI speech in <paramref name="owner"/>'s voice from an enemy.</summary>
    public void HostPlayGenerated(EnemyAI enemy, ulong owner, float[] samples, int sampleRate)
    {
        if (!IsServer || enemy == null || enemy.isEnemyDead || samples.Length == 0) return;

        var clip = new MimicClip(owner, nextGeneratedId++, sampleRate, AudioCodec.EncodeMuLaw(samples));
        SendTransfer(MsgClip, clip.Owner, clip.Id, clip.SampleRate, clip.Encoded, ClientsExcept(owner));

        float pv = Cfg.PitchVariation.Value * 0.5f;
        HostBroadcastPlay(enemy, clip, 1f + Random.Range(-pv, pv), Random.Range(Mathf.Min(Cfg.MinVolume.Value, 1f), 1f), 0, clip.SampleCount);
        StartCoroutine(RemoveGeneratedLater(clip, clip.Duration + 20f));
    }

    private IEnumerator RemoveGeneratedLater(MimicClip clip, float delay)
    {
        yield return new WaitForSeconds(delay);
        HostRemove(clip.Owner, clip.Id, except: clip.Owner);
    }

    private static void PlayLocal(EnemyAI enemy, MimicClip clip, float pitch, float volume, int start, int length)
    {
        if (enemy == null || enemy.isEnemyDead) return;
        start = Mathf.Clamp(start, 0, clip.SampleCount - 1);
        length = Mathf.Clamp(length, 1, clip.SampleCount - start);

        var emitter = enemy.GetComponent<MimicEmitter>();
        if (emitter == null) emitter = enemy.gameObject.AddComponent<MimicEmitter>();
        emitter.Play(clip, start, length, pitch, volume, FindPlayer(clip.Owner));
    }

    public static PlayerControllerB FindPlayer(ulong clientId)
    {
        var round = StartOfRound.Instance;
        if (round == null) return null;
        foreach (var p in round.allPlayerScripts)
        {
            if (p != null && (p.isPlayerControlled || p.isPlayerDead) && p.actualClientId == clientId) return p;
        }
        return null;
    }

    // ---------------------------------------------------------------- queries used by add-ons (via MimicsApi)

    public IEnumerable<MimicEvent> RecentMimics(float withinSeconds) =>
        mimicEvents.Where(e => Time.time - e.Time <= withinSeconds).OrderByDescending(e => e.Time);

    public void MarkBusy(EnemyAI enemy, float seconds)
    {
        if (enemy != null) busyUntil[enemy] = Time.time + seconds;
    }

    /// <summary>The enemy closest to <paramref name="listener"/> that could plausibly be <paramref name="owner"/>.</summary>
    public EnemyAI EnemyThatCouldBe(ulong owner, PlayerControllerB listener)
    {
        var rm = RoundManager.Instance;
        if (rm == null) return null;
        var alive = AlivePlayers();
        return rm.SpawnedEnemies
            .Where(e => e != null && !e.isEnemyDead && e.IsSpawned && IsMimic(e) && !IsBusy(e))
            .Where(e => Vector3.Distance(e.transform.position, listener.transform.position) <= Cfg.HearingRange.Value)
            .Where(e => OwnerWeight(e, owner, alive) > 0f)
            .OrderBy(e => Vector3.Distance(e.transform.position, listener.transform.position))
            .FirstOrDefault();
    }

    private bool IsBusy(EnemyAI enemy) => busyUntil.TryGetValue(enemy, out float t) && Time.time < t;

    private static bool IsExcluded(EnemyAI enemy) => Cfg.IsExcluded(enemy.enemyType != null ? enemy.enemyType.enemyName : null);

    /// <summary>Whether this enemy is one of the level's mimics. Rolled once per enemy against the level's share.</summary>
    private bool IsMimic(EnemyAI enemy)
    {
        if (isMimic.TryGetValue(enemy, out bool mimic)) return mimic;
        if (levelMimicShare < 0f) levelMimicShare = Random.Range(Mathf.Clamp01(Cfg.MinMimicShare.Value), 1f);
        mimic = !IsExcluded(enemy) && Random.value <= levelMimicShare;
        isMimic[enemy] = mimic;
        return mimic;
    }

    private static IEnumerable<PlayerControllerB> ListenersOf(EnemyAI enemy) =>
        AlivePlayers().Where(p => Vector3.Distance(p.transform.position, enemy.transform.position) <= Cfg.HearingRange.Value);

    /// <summary>True while someone in earshot of <paramref name="enemy"/> recently heard a mimic.</summary>
    private bool ListenersOnCooldown(EnemyAI enemy, float now) =>
        ListenersOf(enemy).Any(p => listenerReadyTime.TryGetValue(p.actualClientId, out float t) && now < t);

    private static List<PlayerControllerB> AlivePlayers() =>
        StartOfRound.Instance.allPlayerScripts.Where(p => p != null && p.isPlayerControlled && !p.isPlayerDead).ToList();

    // ---------------------------------------------------------------- host scheduler

    private void Update()
    {
        if (!registered) return;

        if (pending.Count > 0)
        {
            foreach (var key in pending.Where(kv => Time.time - kv.Value.Started > 60f).Select(kv => kv.Key).ToList())
                pending.Remove(key);
        }
        pendingPlays.RemoveAll(p => Time.time - p.Received > 8f);
        mimicEvents.RemoveAll(e => Time.time - e.Time > 60f);

        if (IsServer)
        {
            BroadcastAiState();
            HostTick();
        }
    }

    private void HostTick()
    {
        if (Time.time < nextEvaluation) return;
        nextEvaluation = Time.time + 0.5f;

        var round = StartOfRound.Instance;
        var rm = RoundManager.Instance;
        if (round == null || rm == null) return;
        if (round.inShipPhase)
        {
            // New level next time: re-roll which enemies mimic.
            levelMimicShare = -1f;
            isMimic.Clear();
            listenerReadyTime.Clear();
            return;
        }
        bool anyVoices = store.Count > 0 || (AiRunning && Ai.OwnersWithLines().Any());
        if (!Cfg.MimicsEnabled.Value || !anyVoices) return;

        foreach (var dead in nextMimicTime.Keys.Where(e => e == null || e.isEnemyDead).ToList())
        {
            nextMimicTime.Remove(dead);
            busyUntil.Remove(dead);
        }
        foreach (var dead in isMimic.Keys.Where(e => e == null).ToList()) isMimic.Remove(dead);

        float now = Time.time;
        float minI = Mathf.Min(Cfg.MinInterval.Value, Cfg.MaxInterval.Value);
        float maxI = Mathf.Max(Cfg.MinInterval.Value, Cfg.MaxInterval.Value);

        // Shuffle so the same enemy doesn't always win when several want to talk to the same listener.
        var enemies = rm.SpawnedEnemies.Where(e => e != null && !e.isEnemyDead && e.IsSpawned).OrderBy(_ => Random.value).ToList();
        foreach (var enemy in enemies)
        {
            if (!IsMimic(enemy)) continue;

            if (!nextMimicTime.TryGetValue(enemy, out float due))
            {
                nextMimicTime[enemy] = now + Random.Range(minI * 0.5f, maxI);
                continue;
            }
            if (now < due || IsBusy(enemy) || ListenersOnCooldown(enemy, now)) continue;

            switch (TryMimic(enemy))
            {
                case MimicResult.Played:
                    nextMimicTime[enemy] = now + Random.Range(minI, maxI);
                    break;
                case MimicResult.ChanceFailed:
                    nextMimicTime[enemy] = now + Random.Range(minI, maxI) * 0.5f;
                    break;
                case MimicResult.NoOpportunity:
                    // Nobody to fool right now; check again soon so it speaks when someone wanders close.
                    nextMimicTime[enemy] = now + Random.Range(2f, 5f);
                    break;
            }
        }
    }

    private MimicResult TryMimic(EnemyAI enemy)
    {
        var owners = new HashSet<ulong>(store.Owners);
        if (AiRunning) owners.UnionWith(Ai.OwnersWithLines().Where(AiAllowedFor));

        var owner = PickOwner(enemy, owners);
        if (owner == null) return MimicResult.NoOpportunity;
        if (Random.value > Cfg.MimicChance.Value) return MimicResult.ChanceFailed;

        bool hasClips = store.ClipsOf(owner.Value).Count > 0;
        bool useAi = AiRunning && AiAllowedFor(owner.Value) && Ai.OwnersWithLines().Contains(owner.Value) &&
                     (!hasClips || Random.value < Ai.LineChance);
        if (useAi && Ai.TryTakeLine(owner.Value, out var samples, out int rate))
        {
            HostPlayGenerated(enemy, owner.Value, samples, rate);
            return MimicResult.Played;
        }

        var clip = PickClip(owner.Value, null);
        if (clip == null) return MimicResult.NoOpportunity;

        PlayWithVariation(enemy, clip);

        if (Random.value < Cfg.RepeatChance.Value)
            StartCoroutine(FollowUp(enemy, clip, Random.Range(1.5f, 4f)));

        return MimicResult.Played;
    }

    /// <summary>
    /// Weighted pick of whose voice to use. Owners close to the enemy are excluded (the point is to sound
    /// like someone who isn't there), far/other-area owners are preferred, and someone else must be in earshot.
    /// </summary>
    private ulong? PickOwner(EnemyAI enemy, IEnumerable<ulong> owners)
    {
        var alive = AlivePlayers();
        var candidates = new List<(ulong owner, float weight)>();
        foreach (ulong owner in owners)
        {
            float weight = OwnerWeight(enemy, owner, alive);
            if (weight > 0f) candidates.Add((owner, weight));
        }

        if (candidates.Count == 0) return null;
        float roll = Random.value * candidates.Sum(c => c.weight);
        foreach (var c in candidates)
        {
            roll -= c.weight;
            if (roll <= 0f) return c.owner;
        }
        return candidates[candidates.Count - 1].owner;
    }

    /// <summary>How suitable <paramref name="owner"/>'s voice is for this enemy right now; 0 = not allowed.</summary>
    private static float OwnerWeight(EnemyAI enemy, ulong owner, List<PlayerControllerB> alive)
    {
        var ownerPlayer = FindPlayer(owner);
        if (ownerPlayer == null) return 0f;
        Vector3 enemyPos = enemy.transform.position;

        // Someone other than the owner has to be close enough to hear it.
        var listeners = alive.Where(p => p.actualClientId != owner &&
                                         Vector3.Distance(p.transform.position, enemyPos) <= Cfg.HearingRange.Value).ToList();
        if (listeners.Count == 0) return 0f;

        if (ownerPlayer.isPlayerDead) return Cfg.DeadOwnerWeight.Value;

        bool sameArea = ownerPlayer.isInsideFactory == !enemy.isOutside;
        float ownerDist = sameArea ? Vector3.Distance(ownerPlayer.transform.position, enemyPos) : float.MaxValue;
        float weight;
        if (ownerDist < Cfg.MinOwnerDistance.Value)
        {
            if (!Cfg.AllowNearbyOwnerFallback.Value) return 0f;
            weight = 0.05f;
        }
        else
        {
            weight = 0.3f + 0.7f * Mathf.Clamp01((ownerDist - Cfg.MinOwnerDistance.Value) / 40f);
        }

        // Less convincing if the real person is standing next to whoever is listening.
        float ownerToListener = listeners.Min(p => Vector3.Distance(p.transform.position, ownerPlayer.transform.position));
        if (ownerToListener < 8f) weight *= 0.35f;
        return weight;
    }

    /// <summary>Random clip from the owner, avoiding recently played ones when possible.</summary>
    private MimicClip PickClip(ulong owner, MimicClip avoid)
    {
        var clips = store.ClipsOf(owner);
        if (clips.Count == 0) return null;

        var fresh = clips.Where(c => c != avoid && !recentlyPlayed.Contains((c.Owner, c.Id))).ToList();
        if (fresh.Count > 0) return fresh[Random.Range(0, fresh.Count)];

        // Everything was played recently: take the one played longest ago.
        return clips.OrderBy(c => recentlyPlayed.IndexOf((c.Owner, c.Id))).First();
    }

    private void PlayWithVariation(EnemyAI enemy, MimicClip clip)
    {
        float pv = Cfg.PitchVariation.Value;
        float pitch = 1f + Random.Range(-pv, pv);
        float volume = Random.Range(Mathf.Min(Cfg.MinVolume.Value, 1f), 1f);

        int start = 0;
        int length = clip.SampleCount;
        int minFragment = (int)(0.8f * clip.SampleRate);
        if (clip.SampleCount > minFragment * 2 && Random.value < Cfg.FragmentChance.Value)
        {
            int minLen = Mathf.Max(minFragment, (int)(clip.SampleCount * 0.4f));
            length = Random.Range(minLen, clip.SampleCount + 1);
            start = Random.Range(0, clip.SampleCount - length + 1);
        }

        HostBroadcastPlay(enemy, clip, pitch, volume, start, length);
    }

    private IEnumerator FollowUp(EnemyAI enemy, MimicClip previous, float gap)
    {
        float clipSeconds = previous.Duration;
        yield return new WaitForSeconds(clipSeconds + gap);
        if (enemy == null || enemy.isEnemyDead || store.Get(previous.Owner, previous.Id) == null) yield break;

        // Half the time repeat the same line ("hey... hey!"), otherwise another line from the same person.
        var clip = Random.value < 0.5f ? previous : PickClip(previous.Owner, previous) ?? previous;
        PlayWithVariation(enemy, clip);
    }
}
