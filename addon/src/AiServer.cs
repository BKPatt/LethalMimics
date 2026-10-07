using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace LethalMimics.AI;

/// <summary>
/// Connection to the Python AI server (host only). Starts it once per game launch if needed so the models
/// stay loaded between lobbies; the server exits on its own when the game process does.
/// </summary>
internal static class AiServer
{
    private static HttpClient http;
    private static Process process;
    private static bool startAttempted;
    private static string baseUrl;

    /// <summary>Last state reported by /health: off, starting, loading, ready, error.</summary>
    public static string State { get; private set; } = "off";
    public static string Error { get; private set; } = "";
    public static bool Ready => State == "ready";

    public static string LogPath => Path.Combine(Paths.BepInExRootPath, "LethalMimics_ai_server.log");

    public static void EnsureStarted()
    {
        var cfg = AiPlugin.Cfg;
        if (http == null)
        {
            http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            Application.quitting += Shutdown;
        }
        baseUrl = cfg.ServerUrl.Value.TrimEnd('/');
        if (startAttempted) return;
        startAttempted = true;
        State = "starting";

        Task.Run(async () =>
        {
            var health = await TryHealth();
            if (health != null)
            {
                AiPlugin.Log.LogInfo($"Using AI server already running at {baseUrl}.");
                return;
            }
            if (!cfg.AutoStartServer.Value)
            {
                Fail($"AI server not reachable at {baseUrl} and AutoStartServer is off.");
                return;
            }
            Launch();
        });
    }

    private static void Launch()
    {
        var cfg = AiPlugin.Cfg;
        string python = Resolve(cfg.PythonPath.Value, DefaultPython(cfg.PluginDir));
        string script = Resolve(cfg.ServerScript.Value, Path.Combine(cfg.PluginDir, "ai_server", "server.py"));
        if (!File.Exists(python)) { Fail($"Python not found at '{python}'. Run ai_server/setup.ps1 (see README) or set PythonPath."); return; }
        if (!File.Exists(script)) { Fail($"Server script not found at '{script}'. Set ServerScript."); return; }

        var uri = new Uri(baseUrl);
        var args = new StringBuilder();
        void Arg(string name, string value) => args.Append(' ').Append(name).Append(' ').Append(Quote(value));

        args.Append(Quote(script));
        Arg("--host", uri.Host);
        Arg("--port", uri.Port.ToString());
        Arg("--tts-model", cfg.TtsModel.Value);
        Arg("--asr-model", cfg.AsrModel.Value);
        Arg("--device", cfg.Device.Value);
        Arg("--llm-api", cfg.LlmApi.Value);
        Arg("--llm-base", cfg.LlmBaseUrl.Value);
        Arg("--llm-model", cfg.LlmModel.Value);
        // No API key here: the server reads it from LETHALMIMICS_LLM_API_KEY or %LOCALAPPDATA%/LethalMimics/llm_api_key.txt,
        // so it never sits in the profile's config folder or on a command line.
        Arg("--language", cfg.Language.Value);
        Arg("--max-ref-seconds", cfg.MaxReferenceSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Arg("--min-ref-seconds", cfg.MinReferenceSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (cfg.CacheVoicesAcrossGames.Value)
            Arg("--cache-dir", Resolve(cfg.VoiceCacheDir.Value, Path.Combine(AiConfig.DataDir, "voice_cache")));
        if (!string.IsNullOrEmpty(cfg.ModelDownloadDir.Value)) Arg("--hf-home", cfg.ModelDownloadDir.Value);
        Arg("--parent-pid", Process.GetCurrentProcess().Id.ToString());
        Arg("--log-file", LogPath);

        try
        {
            process = Process.Start(new ProcessStartInfo(python, args.ToString())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(script) ?? "",
            });
            State = "loading";
            AiPlugin.Log.LogInfo($"Started AI server (pid {process?.Id}). Log: {LogPath}");
        }
        catch (Exception e)
        {
            Fail($"Could not start AI server: {e.Message}");
        }
    }

    /// <summary>setup.ps1's default location (survives mod updates), then a venv next to the server script.</summary>
    private static string DefaultPython(string pluginDir)
    {
        string[] candidates =
        {
            Path.Combine(AiConfig.DataDir, "venv", "Scripts", "python.exe"),
            Path.Combine(pluginDir, "ai_server", ".venv", "Scripts", "python.exe"),
        };
        foreach (string c in candidates)
            if (File.Exists(c)) return c;
        return candidates[0];
    }

    private static string Resolve(string configured, string fallback) =>
        string.IsNullOrWhiteSpace(configured) ? fallback : Environment.ExpandEnvironmentVariables(configured.Trim());

    private static string Quote(string s)
    {
        if (string.IsNullOrEmpty(s)) return "\"\"";
        if (s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return s;
        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in s)
        {
            if (c == '\\') { slashes++; continue; }
            sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }

    private static void Fail(string message)
    {
        State = "error";
        Error = message;
        AiPlugin.Log.LogError(message);
    }

    /// <summary>Polls /health; returns the parsed status or null if unreachable.</summary>
    public static async Task<JObject> TryHealth()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var resp = await http.GetAsync(baseUrl + "/health", cts.Token);
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
            State = (string)json["state"] ?? "loading";
            Error = (string)json["error"] ?? "";
            return json;
        }
        catch
        {
            if (State == "ready") State = "loading";
            if (process != null && process.HasExited && State != "error")
                Fail($"AI server exited (code {process.ExitCode}). See {LogPath}");
            return null;
        }
    }

    public static async Task<JObject> Post(string path, JObject body, float timeoutSeconds)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync(baseUrl + path, content, cts.Token);
        string text = await resp.Content.ReadAsStringAsync();
        var json = JObject.Parse(text);
        if (!resp.IsSuccessStatusCode) throw new Exception((string)json["error"] ?? resp.StatusCode.ToString());
        return json;
    }

    private static void Shutdown()
    {
        try
        {
            if (process != null && !process.HasExited) process.Kill();
        }
        catch (Exception e)
        {
            AiPlugin.Log.LogDebug($"AI server shutdown: {e.Message}");
        }
    }
}
