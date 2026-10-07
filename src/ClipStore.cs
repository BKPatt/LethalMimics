using System.Collections.Generic;
using System.Linq;

namespace LethalMimics;

internal sealed class MimicClip
{
    public readonly ulong Owner;
    public readonly uint Id;
    public readonly int SampleRate;
    public readonly byte[] Encoded;

    public MimicClip(ulong owner, uint id, int sampleRate, byte[] encoded)
    {
        Owner = owner;
        Id = id;
        SampleRate = sampleRate;
        Encoded = encoded;
    }

    public int SampleCount => Encoded.Length;
    public float Duration => (float)Encoded.Length / SampleRate;
}

/// <summary>Every client keeps other players' clips; the host keeps everyone's and is the source of truth.</summary>
internal sealed class ClipStore
{
    private readonly Dictionary<ulong, List<MimicClip>> byOwner = new();

    public int Count => byOwner.Values.Sum(l => l.Count);
    public IEnumerable<ulong> Owners => byOwner.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key);
    public IEnumerable<MimicClip> All => byOwner.Values.SelectMany(l => l);

    public IReadOnlyList<MimicClip> ClipsOf(ulong owner) =>
        byOwner.TryGetValue(owner, out var list) ? list : (IReadOnlyList<MimicClip>)System.Array.Empty<MimicClip>();

    public MimicClip Get(ulong owner, uint id) =>
        byOwner.TryGetValue(owner, out var list) ? list.FirstOrDefault(c => c.Id == id) : null;

    /// <summary>Adds a clip (ignoring duplicates). Returns clips evicted to respect <paramref name="maxPerOwner"/>.</summary>
    public List<MimicClip> Add(MimicClip clip, int maxPerOwner = int.MaxValue)
    {
        var evicted = new List<MimicClip>();
        if (!byOwner.TryGetValue(clip.Owner, out var list))
        {
            list = new List<MimicClip>();
            byOwner[clip.Owner] = list;
        }
        if (list.Any(c => c.Id == clip.Id)) return evicted;
        list.Add(clip);
        while (list.Count > maxPerOwner)
        {
            evicted.Add(list[0]);
            list.RemoveAt(0);
        }
        return evicted;
    }

    public void Remove(ulong owner, uint id)
    {
        if (byOwner.TryGetValue(owner, out var list)) list.RemoveAll(c => c.Id == id);
    }

    public void RemoveAll(ulong owner) => byOwner.Remove(owner);
}
