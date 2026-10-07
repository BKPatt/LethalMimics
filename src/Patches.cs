using HarmonyLib;

namespace LethalMimics;

[HarmonyPatch]
internal static class Patches
{
    // StartOfRound exists for the whole lobby session, so our components live and die with it.
    [HarmonyPatch(typeof(StartOfRound), "Start")]
    [HarmonyPostfix]
    private static void StartOfRoundStart(StartOfRound __instance)
    {
        if (__instance.GetComponent<MimicManager>() == null) __instance.gameObject.AddComponent<MimicManager>();
        if (__instance.GetComponent<VoiceRecorder>() == null) __instance.gameObject.AddComponent<VoiceRecorder>();
        if (__instance.GetComponent<VoiceStreamer>() == null) __instance.gameObject.AddComponent<VoiceStreamer>();
    }

    // The game recomputes push-to-talk mute every frame; force mute while a mimic clip is being recorded
    // so nobody hears it live (this also stops enemies "hearing" the recording).
    [HarmonyPatch(typeof(StartOfRound), "Update")]
    [HarmonyPostfix]
    private static void StartOfRoundUpdate(StartOfRound __instance)
    {
        if (!VoiceRecorder.IsRecording || __instance.voiceChatModule == null) return;
        __instance.voiceChatModule.IsMuted = true;
        if (HUDManager.Instance != null && HUDManager.Instance.PTTIcon != null) HUDManager.Instance.PTTIcon.enabled = false;
    }
}
