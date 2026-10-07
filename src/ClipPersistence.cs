using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;

namespace LethalMimics;

/// <summary>Saves the local player's own clips so they carry over between lobbies.</summary>
internal static class ClipPersistence
{
    private const uint Magic = 0x314D414C; // "LAM1"
    private static string Folder => Path.Combine(MimicConfig.DataDir, "clips");

    public readonly struct SavedClip
    {
        public readonly string Path;
        public readonly int SampleRate;
        public readonly byte[] Encoded;

        public SavedClip(string path, int sampleRate, byte[] encoded)
        {
            Path = path;
            SampleRate = sampleRate;
            Encoded = encoded;
        }
    }

    public static string Save(int sampleRate, byte[] encoded)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, $"{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.lamc");
            using var w = new BinaryWriter(File.Create(path));
            w.Write(Magic);
            w.Write(sampleRate);
            w.Write(encoded.Length);
            w.Write(encoded);
            return path;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Could not save clip: {e.Message}");
            return null;
        }
    }

    /// <summary>Loads saved clips oldest-first, deleting any beyond <paramref name="max"/>.</summary>
    public static List<SavedClip> LoadAll(int max)
    {
        var result = new List<SavedClip>();
        if (!Directory.Exists(Folder)) return result;

        var files = Directory.GetFiles(Folder, "*.lamc").OrderBy(f => f, StringComparer.Ordinal).ToList();
        while (files.Count > max)
        {
            Delete(files[0]);
            files.RemoveAt(0);
        }

        foreach (string path in files)
        {
            try
            {
                using var r = new BinaryReader(File.OpenRead(path));
                if (r.ReadUInt32() != Magic) continue;
                int rate = r.ReadInt32();
                int len = r.ReadInt32();
                if (rate < 4000 || rate > 48000 || len <= 0 || len > 48000 * 30) continue;
                result.Add(new SavedClip(path, rate, r.ReadBytes(len)));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Skipping unreadable clip {Path.GetFileName(path)}: {e.Message}");
            }
        }
        return result;
    }

    public static void Delete(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { File.Delete(path); }
        catch (Exception e) { Plugin.Log.LogWarning($"Could not delete clip {path}: {e.Message}"); }
    }
}
