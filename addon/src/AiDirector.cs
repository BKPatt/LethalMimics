using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GameNetcodeStuff;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Random = UnityEngine.Random;

namespace LethalMimics.AI;

/// <summary>
/// Host-only brain glue. Feeds every player's speech to the AI server (which transcribes it and keeps
/// improving that player's voice clone), keeps a couple of ready-to-play AI lines per player for the mimic
/// scheduler, and makes enemies answer when someone talks to the person they are pretending to be.
/// </summary>
internal sealed class AiDirector : MonoBehaviour, IMimicVoiceAi
{
    private const int LinesPerPlayer = 2;
    private const float LineMaxAge = 240f;
    private const int MaxIngestsInFlight = 6;

    private sealed class ReadyLine
    {
        public float[] Samples;
        public int SampleRate;
        public string Text;
        public float Created;
    }

    private sealed class PlayerAiStatus
    {
        public bool Ready;
        public float RefSeconds;
        public float Quality;
    }

    private readonly ConcurrentQueue<Action> mainThread = new();
    private readonly Dictionary<ulong, Queue<ReadyLine>> lines = new();
    private readonly Dictionary<string, PlayerAiStatus> status = new();
    private readonly HashSet<EnemyAI> replying = new();
    private string lastRoster = "";
    private float nextPoll;
    private bool generating;
    private float generateRetryAt;
    private int ingestsInFlight;
    private string lastState = "";

    private static AiConfig Cfg => AiPlugin.Cfg;

    public bool Active => enabled;
    public float LineChance => Cfg.LineChance.Value;

    private void Awake() => MimicsApi.RegisterVoiceAi(this);

    private void OnDestroy() => MimicsApi.UnregisterVoiceAi(this);

    private void Start()
    {
        AiServer.EnsureStarted();
        MimicsApi.Notify(AiServer.Ready ? "AI voice cloning is ON (host)" : "AI voice cloning is ON (host). Loading models...", 5f);
    }

    private void OnDisable()
    {
        // Switched off in game: forget queued lines and any conversation in progress.
        lines.Clear();
        replying.Clear();
    }

    private void Update()
    {
        while (mainThread.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception e) { AiPlugin.Log.LogError($"AI callback failed: {e}"); }
        }

        if (Time.time >= nextPoll)
        {
            nextPoll = Time.time + 3f;
            Poll();
        }

        if (AiServer.Ready) KeepLinesStocked();
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Runs a server call off the main thread and hands the result back on the main thread.</summary>
    private void Call(string path, JObject body, float timeout, Action<JObject> onOk, Action<string> onError = null)
    {
        Task.Run(async () =>
        {
            try
            {
                var result = await AiServer.Post(path, body, timeout);
                mainThread.Enqueue(() => onOk(result));
            }
            catch (Exception e)
            {
                string msg = e.InnerException?.Message ?? e.Message;
                mainThread.Enqueue(() =>
                {
                    AiPlugin.Log.LogDebug($"AI {path} failed: {msg}");
                    onError?.Invoke(msg);
                });
            }
        });
    }

    public static string PlayerKey(PlayerControllerB p) =>
        p.playerSteamId != 0 ? p.playerSteamId.ToString() : "lan-" + p.playerUsername;

    private static IEnumerable<PlayerControllerB> ConnectedPlayers()
    {
        var round = StartOfRound.Instance;
        if (round == null) return Enumerable.Empty<PlayerControllerB>();
        return round.allPlayerScripts.Where(p => p != null && (p.isPlayerControlled || p.isPlayerDead));
    }

    private static bool Allowed(ulong clientId) => MimicsApi.VoiceAllowedFor(clientId);

    private bool IsVoiceReady(PlayerControllerB p) =>
        status.TryGetValue(PlayerKey(p), out var s) && s.Ready;

    private static string Situation(EnemyAI enemy, PlayerControllerB listener)
    {
        var round = StartOfRound.Instance;
        string moon = round != null && round.currentLevel != null ? round.currentLevel.PlanetName : "unknown moon";
        var alive = ConnectedPlayers().Where(p => !p.isPlayerDead).Select(p => p.playerUsername).ToList();
        var dead = ConnectedPlayers().Where(p => p.isPlayerDead).Select(p => p.playerUsername).ToList();
        string where = enemy == null ? "somewhere on the moon" : enemy.isOutside ? "outside, near the facility" : "inside the facility";
        return $"On {moon}, you are {where}. Alive crew: {string.Join(", ", alive)}." +
               (dead.Count > 0 ? $" Dead: {string.Join(", ", dead)}." : "") +
               (listener != null ? $" {listener.playerUsername} is close to you and can hear you." : "");
    }

    // ------------------------------------------------------------------ status / roster

    private void Poll()
    {
        Task.Run(async () =>
        {
            var health = await AiServer.TryHealth();
            mainThread.Enqueue(() => OnHealth(health));
        });

        if (!AiServer.Ready) return;
        var roster = ConnectedPlayers().Where(p => Allowed(p.actualClientId))
            .Select(p => new JObject { ["key"] = PlayerKey(p), ["name"] = p.playerUsername }).ToList();
        string sig = string.Join("|", roster.Select(r => (string)r["key"] + (string)r["name"]));
        if (sig == lastRoster) return;
        lastRoster = sig;
        Call("/roster", new JObject { ["players"] = new JArray(roster) }, 30f, OnHealth);
    }

    private void OnHealth(JObject health)
    {
        string state = AiServer.State;
        if (state != lastState)
        {
            lastState = state;
            if (state == "ready") MimicsApi.Notify("AI voices ready - enemies will learn everyone's voice as they talk", 5f);
            else if (state == "error") MimicsApi.Notify($"AI voices failed: {AiServer.Error}", 10f);
        }
        if (health?["players"] is not JObject players) return;

        foreach (var prop in players.Properties())
        {
            var s = prop.Value;
            bool wasReady = status.TryGetValue(prop.Name, out var old) && old.Ready;
            var now = new PlayerAiStatus
            {
                Ready = (bool?)s["ready"] ?? false,
                RefSeconds = (float?)s["ref_seconds"] ?? 0f,
                Quality = (float?)s["quality"] ?? 0f,
            };
            status[prop.Name] = now;
            if (now.Ready && !wasReady)
                AiPlugin.Log.LogInfo($"Voice clone ready for {prop.Name}: {now.RefSeconds:0.0}s reference, quality {now.Quality:0.00}");
        }
    }

    // ------------------------------------------------------------------ speech in

    /// <summary>A player's utterance arrived (from the network, or the host's own mic).</summary>
    public void OnSpeech(ulong clientId, float[] samples, int sampleRate) => Ingest(clientId, samples, sampleRate, "voice");

    /// <summary>A recorded mimic clip also makes good cloning material.</summary>
    public void OnRecordedClip(ulong clientId, float[] samples, int sampleRate) => Ingest(clientId, samples, sampleRate, "clip");

    private void Ingest(ulong clientId, float[] samples, int sampleRate, string source)
    {
        if (!enabled || !AiServer.Ready || !Allowed(clientId) || ingestsInFlight >= MaxIngestsInFlight) return;
        var player = MimicsApi.FindPlayer(clientId);
        if (player == null) return;

        ingestsInFlight++;
        var body = new JObject
        {
            ["key"] = PlayerKey(player),
            ["name"] = player.playerUsername,
            ["sample_rate"] = sampleRate,
            ["source"] = source,
            ["pcm16"] = ToPcm16Base64(samples),
        };
        Call("/ingest", body, 60f,
            result =>
            {
                ingestsInFlight--;
                string text = (string)result["text"] ?? "";
                if (source == "voice" && text.Length > 0)
                    OnHeard(clientId, text, result["addressed"]?.ToObject<List<string>>() ?? new List<string>());
            },
            _ => ingestsInFlight--);
    }

    private static string ToPcm16Base64(float[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short v = (short)Mathf.Clamp(samples[i] * 32767f, -32768f, 32767f);
            bytes[i * 2] = (byte)v;
            bytes[i * 2 + 1] = (byte)(v >> 8);
        }
        return Convert.ToBase64String(bytes);
    }

    private static float[] FromPcm16Base64(string b64)
    {
        var bytes = Convert.FromBase64String(b64);
        var samples = new float[bytes.Length / 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8)) / 32768f;
        return samples;
    }

    // ------------------------------------------------------------------ replies

    /// <summary>
    /// Someone said something. If they are near an enemy that just spoke as another player (or they called out
    /// a far-away player's name near an enemy), have that enemy answer in the other player's cloned voice.
    /// </summary>
    private void OnHeard(ulong speakerId, string text, List<string> addressedKeys)
    {
        var speaker = MimicsApi.FindPlayer(speakerId);
        if (speaker == null || speaker.isPlayerDead) return;

        EnemyAI enemy = null;
        PlayerControllerB target = null;

        // 1) Answering a mimic they just heard.
        foreach (var ev in MimicsApi.RecentMimics(Cfg.ReplyWindowSeconds.Value))
        {
            if (ev.Owner == speakerId || ev.Enemy == null || ev.Enemy.isEnemyDead) continue;
            if (Vector3.Distance(speaker.transform.position, ev.Enemy.transform.position) > MimicsApi.HearingRange) continue;
            var owner = MimicsApi.FindPlayer(ev.Owner);
            if (owner == null || !Allowed(ev.Owner) || !IsVoiceReady(owner)) continue;
            enemy = ev.Enemy;
            target = owner;
            break;
        }

        // 2) Calling out someone's name while an enemy is lurking nearby and that person is far from it.
        if (enemy == null && Cfg.ReplyToNameMentions.Value && addressedKeys.Count > 0)
        {
            foreach (var p in ConnectedPlayers())
            {
                if (p.actualClientId == speakerId || !addressedKeys.Contains(PlayerKey(p))) continue;
                if (!Allowed(p.actualClientId) || !IsVoiceReady(p)) continue;
                var candidate = MimicsApi.EnemyThatCouldBe(p.actualClientId, speaker);
                if (candidate == null) continue;
                enemy = candidate;
                target = p;
                break;
            }
        }

        if (enemy == null || replying.Contains(enemy) || Random.value > Cfg.ReplyChance.Value) return;

        replying.Add(enemy);
        MimicsApi.MarkBusy(enemy, 15f);
        float asked = Time.time;
        ulong targetId = target.actualClientId;
        var body = new JObject
        {
            ["key"] = PlayerKey(target),
            ["name"] = target.playerUsername,
            ["speaker_name"] = speaker.playerUsername,
            ["heard"] = text,
            ["situation"] = Situation(enemy, speaker),
            ["sample_rate"] = 24000,
        };
        Call("/respond", body, 30f,
            result =>
            {
                replying.Remove(enemy);
                if (enemy == null) return;
                MimicsApi.MarkBusy(enemy, 0f);
                if (!enabled || enemy.isEnemyDead || Time.time - asked > 20f) return;
                MimicsApi.PlayGenerated(enemy, targetId, FromPcm16Base64((string)result["pcm16"]), (int)result["sample_rate"]);
            },
            _ =>
            {
                replying.Remove(enemy);
                if (enemy != null) MimicsApi.MarkBusy(enemy, 0f);
            });
    }

    // ------------------------------------------------------------------ pre-generated lines for the scheduler

    public IEnumerable<ulong> OwnersWithLines() =>
        lines.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key);

    public bool TryTakeLine(ulong owner, out float[] samples, out int sampleRate)
    {
        samples = null;
        sampleRate = 0;
        if (!lines.TryGetValue(owner, out var q) || q.Count == 0) return false;
        var line = q.Dequeue();
        samples = line.Samples;
        sampleRate = line.SampleRate;
        return true;
    }

    private void KeepLinesStocked()
    {
        foreach (var q in lines.Values)
            while (q.Count > 0 && Time.time - q.Peek().Created > LineMaxAge) q.Dequeue();

        if (generating || Time.time < generateRetryAt) return;
        var round = StartOfRound.Instance;
        if (round == null || round.inShipPhase) return; // only bother during a landed round

        var player = ConnectedPlayers()
            .Where(p => Allowed(p.actualClientId) && IsVoiceReady(p))
            .Where(p => !lines.TryGetValue(p.actualClientId, out var q) || q.Count < LinesPerPlayer)
            .OrderBy(p => lines.TryGetValue(p.actualClientId, out var q) ? q.Count : 0)
            .FirstOrDefault();
        if (player == null) return;

        generating = true;
        ulong owner = player.actualClientId;
        var body = new JObject
        {
            ["key"] = PlayerKey(player),
            ["name"] = player.playerUsername,
            ["situation"] = Situation(null, null),
            ["sample_rate"] = 24000,
            ["log_as_said"] = false,
        };
        Call("/line", body, 60f,
            result =>
            {
                generating = false;
                if (!enabled) return;
                if (!lines.TryGetValue(owner, out var q)) lines[owner] = q = new Queue<ReadyLine>();
                q.Enqueue(new ReadyLine
                {
                    Samples = FromPcm16Base64((string)result["pcm16"]),
                    SampleRate = (int)result["sample_rate"],
                    Text = (string)result["text"],
                    Created = Time.time,
                });
            },
            _ =>
            {
                generating = false;
                generateRetryAt = Time.time + 10f;
            });
    }

    public void OnPlayerLeft(ulong clientId) => lines.Remove(clientId);
}
