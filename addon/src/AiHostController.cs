using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LethalMimics.AI;

/// <summary>
/// Per-lobby, host only. Owns the <see cref="AiDirector"/> and the in-game toggle key, so the host can switch
/// AI cloning on/off without leaving the lobby. Switching off keeps the models loaded so it can resume instantly.
/// </summary>
internal sealed class AiHostController : MonoBehaviour
{
    private AiDirector director;
    private InputAction toggleAction;

    private void Start()
    {
        if (AiPlugin.Cfg.Enabled.Value) SetRunning(true);

        string key = AiPlugin.Cfg.ToggleKey.Value;
        if (string.IsNullOrWhiteSpace(key)) return;
        try
        {
            toggleAction = new InputAction("LethalMimicsAI.Toggle", InputActionType.Button, key.Trim());
            toggleAction.Enable();
        }
        catch (Exception e)
        {
            AiPlugin.Log.LogWarning($"Invalid ToggleKey '{key}': {e.Message}");
        }
    }

    private void Update()
    {
        if (toggleAction != null && toggleAction.WasPressedThisFrame() && !IsTyping())
        {
            bool on = director == null || !director.enabled;
            SetRunning(on);
            MimicsApi.Notify(on ? "AI voice cloning turned ON for this lobby" : "AI voice cloning turned OFF for this lobby (models stay loaded)", 4f);
        }
    }

    private void SetRunning(bool on)
    {
        if (on)
        {
            if (director == null) director = gameObject.AddComponent<AiDirector>();
            else director.enabled = true;
        }
        else if (director != null)
        {
            director.enabled = false;
        }
        MimicsApi.VoiceAiStateChanged();
    }

    private static bool IsTyping()
    {
        var player = GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null;
        return player != null && (player.isTypingChat || player.inTerminalMenu);
    }

    private void OnDestroy() => toggleAction?.Dispose();
}
