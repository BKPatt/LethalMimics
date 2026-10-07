using System;

namespace LethalMimics;

/// <summary>Small DSP helpers plus G.711 mu-law (8 bits/sample) so clips are cheap to send.</summary>
internal static class AudioCodec
{
    private const int MuLawBias = 0x84;
    private const int MuLawClip = 32635;
    private static readonly float[] MuLawDecodeTable = BuildDecodeTable();

    public static float[] Downmix(float[] interleaved, int channels)
    {
        if (channels <= 1) return interleaved;
        int frames = interleaved.Length / channels;
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float sum = 0f;
            for (int c = 0; c < channels; c++) sum += interleaved[f * channels + c];
            mono[f] = sum / channels;
        }
        return mono;
    }

    /// <summary>Box-filter decimation when downsampling, linear interpolation when upsampling.</summary>
    public static float[] Resample(float[] src, int srcRate, int dstRate)
    {
        if (srcRate == dstRate || src.Length == 0) return src;
        double ratio = (double)srcRate / dstRate;
        int outLen = (int)(src.Length / ratio);
        var dst = new float[outLen];
        if (ratio > 1.0)
        {
            for (int i = 0; i < outLen; i++)
            {
                int start = (int)(i * ratio);
                int end = Math.Min(src.Length, (int)((i + 1) * ratio));
                if (end <= start) end = start + 1;
                float sum = 0f;
                for (int j = start; j < end; j++) sum += src[j];
                dst[i] = sum / (end - start);
            }
        }
        else
        {
            for (int i = 0; i < outLen; i++)
            {
                double pos = i * ratio;
                int i0 = (int)pos;
                int i1 = Math.Min(i0 + 1, src.Length - 1);
                float t = (float)(pos - i0);
                dst[i] = src[i0] + (src[i1] - src[i0]) * t;
            }
        }
        return dst;
    }

    /// <summary>Cuts leading/trailing audio whose 10ms mean level is below the threshold.</summary>
    public static float[] TrimSilence(float[] s, int rate, float threshold = 0.012f, float padSeconds = 0.12f)
    {
        int win = Math.Max(1, rate / 100);
        int windows = s.Length / win;
        if (windows == 0) return s;

        int first = -1, last = -1;
        for (int w = 0; w < windows; w++)
        {
            float sum = 0f;
            for (int i = w * win; i < (w + 1) * win; i++) sum += Math.Abs(s[i]);
            if (sum / win > threshold)
            {
                if (first < 0) first = w;
                last = w;
            }
        }
        if (first < 0) return Array.Empty<float>();

        int pad = (int)(padSeconds * rate);
        int start = Math.Max(0, first * win - pad);
        int end = Math.Min(s.Length, (last + 1) * win + pad);
        var result = new float[end - start];
        Array.Copy(s, start, result, 0, result.Length);
        return result;
    }

    public static void Normalize(float[] s, float targetPeak = 0.9f, float maxGain = 4f)
    {
        float peak = 0f;
        foreach (float v in s) peak = Math.Max(peak, Math.Abs(v));
        if (peak < 1e-4f) return;
        float gain = Math.Min(maxGain, targetPeak / peak);
        for (int i = 0; i < s.Length; i++) s[i] *= gain;
    }

    public static void ApplyFades(float[] s, int fadeSamples)
    {
        int n = Math.Min(fadeSamples, s.Length / 2);
        for (int i = 0; i < n; i++)
        {
            float g = (float)i / n;
            s[i] *= g;
            s[s.Length - 1 - i] *= g;
        }
    }

    public static byte[] EncodeMuLaw(float[] samples)
    {
        var bytes = new byte[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            float f = samples[i];
            if (f > 1f) f = 1f; else if (f < -1f) f = -1f;
            bytes[i] = LinearToMuLaw((int)(f * 32767f));
        }
        return bytes;
    }

    public static float[] DecodeMuLaw(byte[] bytes, int offset, int count)
    {
        var samples = new float[count];
        for (int i = 0; i < count; i++) samples[i] = MuLawDecodeTable[bytes[offset + i]];
        return samples;
    }

    private static byte LinearToMuLaw(int sample)
    {
        int sign = sample < 0 ? 0x80 : 0;
        if (sign != 0) sample = -sample;
        if (sample > MuLawClip) sample = MuLawClip;
        sample += MuLawBias;

        int exponent = 7;
        for (int mask = 0x4000; (sample & mask) == 0 && exponent > 0; mask >>= 1) exponent--;
        int mantissa = (sample >> (exponent + 3)) & 0x0F;
        return (byte)~(sign | (exponent << 4) | mantissa);
    }

    private static float[] BuildDecodeTable()
    {
        var table = new float[256];
        for (int i = 0; i < 256; i++)
        {
            int mu = ~i & 0xFF;
            int sign = mu & 0x80;
            int exponent = (mu >> 4) & 0x07;
            int mantissa = mu & 0x0F;
            int sample = (((mantissa << 3) + MuLawBias) << exponent) - MuLawBias;
            table[i] = (sign != 0 ? -sample : sample) / 32768f;
        }
        return table;
    }
}
