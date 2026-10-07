using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace LethalMimics.AI;

/// <summary>
/// Optional host-side add-on for LethalMimics: clones players' voices with a local Python server (TTS + speech
/// recognition + a small LLM) and lets enemies speak and answer as them. Does nothing unless you host.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(LethalMimicsGuid, BepInDependency.DependencyFlags.HardDependency)]
public class AiPlugin : BaseUnityPlugin
{
    public const string Guid = "brant.LethalMimics.AI";
    public const string Name = "LethalMimics AI";
    public const string Version = "1.0.0";
    private const string LethalMimicsGuid = "brant.LethalMimics";

    internal static ManualLogSource Log;
    internal static AiConfig Cfg;

    private void Awake()
    {
        Log = Logger;
        Cfg = new AiConfig(Config);
        MimicsApi.HostSessionStarted += OnHostSessionStarted;
        Log.LogInfo($"{Name} {Version} loaded. AI voice cloning is {(Cfg.Enabled.Value ? "on" : "off")} when you host" +
                    (string.IsNullOrWhiteSpace(Cfg.ToggleKey.Value) ? "." : $"; toggle in game with {Cfg.ToggleKey.Value}."));
    }

    private static void OnHostSessionStarted(GameObject session)
    {
        if (session.GetComponent<AiHostController>() == null) session.AddComponent<AiHostController>();
    }
}
