using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace LethalMimics;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "brant.LethalMimics";
    public const string Name = "LethalMimics";
    public const string Version = "1.0.1";

    internal static ManualLogSource Log;
    internal static MimicConfig Cfg;

    private void Awake()
    {
        Log = Logger;
        Cfg = new MimicConfig(Config);
        new Harmony(Guid).PatchAll(typeof(Patches));
        Log.LogInfo($"{Name} {Version} loaded. Hold {Cfg.RecordKey.Value} to record mimic voice clips.");
    }
}
