using System.Media;

namespace ZendeskWatcher.UI;

/// <summary>
/// Short chiptune jingles in the spirit of Amiga tracker music, synthesized in code (pulse waves, fast arpeggios,
/// a soft low-pass like the Amiga's audio filter). Original little motifs, not taken from any existing tune.
/// Windows toasts can't play custom sounds for an unpackaged app, so these are played by the app itself.
/// </summary>
public static class ChipSounds
{
    private const int SampleRate = 22050;
    private static readonly Dictionary<ToastSound, byte[]> Cache = [];
    private static SoundPlayer? _player; // kept so it isn't collected while playing

    public static bool IsChip(ToastSound sound) => sound is ToastSound.ChipBlip or ToastSound.ChipArpeggio
        or ToastSound.ChipBounce or ToastSound.ChipAlert or ToastSound.ChipFanfare;

    public static void Play(ToastSound sound)
    {
        try
        {
            if (!Cache.TryGetValue(sound, out var wav))
                Cache[sound] = wav = Wav(Synthesize(sound));
            _player?.Dispose();
            _player = new SoundPlayer(new MemoryStream(wav));
            _player.Play();
        }
        catch (Exception ex)
        {
            Log.Error("Could not play chiptune sound", ex);
        }
    }

    /// <summary>One note: MIDI number, length, pulse width, and optional arpeggio offsets (semitones, tracker-style).</summary>
    private readonly record struct Note(int Midi, int Ms, double Duty = 0.25, int[]? Arp = null, double Volume = 1.0);

    private static Note[] Tune(ToastSound sound) => sound switch
    {
        // Two quick rising blips: light, for frequent notifications.
        ToastSound.ChipBlip => [new(84, 55, 0.125), new(91, 90, 0.125)],
        // A fast run up a major chord, ending in a shimmering arpeggio.
        ToastSound.ChipArpeggio => [new(72, 45), new(76, 45), new(79, 45), new(84, 230, 0.5, [0, 4, 7])],
        // A playful down-up bounce.
        ToastSound.ChipBounce => [new(88, 45), new(84, 45), new(88, 45), new(91, 140, 0.25, [0, 12])],
        // Two alternating tones: something needs attention.
        ToastSound.ChipAlert => [new(81, 75, 0.5), new(88, 75, 0.5), new(81, 75, 0.5), new(88, 75, 0.5), new(81, 75, 0.5), new(88, 150, 0.5)],
        // A short triumphant phrase with a minor-to-major lift.
        ToastSound.ChipFanfare => [new(67, 70), new(70, 70), new(74, 70), new(79, 120, 0.25, [0, 3, 7]), new(79, 260, 0.5, [0, 4, 7])],
        _ => []
    };

    private static float[] Synthesize(ToastSound sound)
    {
        var samples = new List<float>();
        foreach (var note in Tune(sound))
        {
            var count = SampleRate * note.Ms / 1000;
            var phase = 0.0;
            for (var i = 0; i < count; i++)
            {
                var t = (double)i / SampleRate;
                // Tracker arpeggio: switch between the offsets every 20 ms (one "tick" at 50 Hz).
                var offset = note.Arp is { Length: > 0 } arp ? arp[(int)(t / 0.02) % arp.Length] : 0;
                // A little vibrato on longer notes.
                var vibrato = note.Ms > 150 ? 0.15 * Math.Sin(2 * Math.PI * 6 * t) : 0;
                var freq = 440 * Math.Pow(2, (note.Midi + offset + vibrato - 69) / 12.0);
                phase = (phase + freq / SampleRate) % 1.0;

                var pulse = phase < note.Duty ? 1.0 : -1.0;
                var attack = Math.Min(1.0, t / 0.003);
                var decay = Math.Exp(-3.0 * i / count);
                samples.Add((float)(pulse * attack * decay * note.Volume));
            }
        }

        // Soft one-pole low-pass (rounds off the square edges) and overall level.
        var smoothed = new float[samples.Count];
        var last = 0.0;
        for (var i = 0; i < samples.Count; i++)
        {
            last += 0.35 * (samples[i] - last);
            smoothed[i] = (float)(last * 0.28);
        }
        return smoothed;
    }

    /// <summary>16-bit mono PCM WAV.</summary>
    private static byte[] Wav(float[] samples)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var dataBytes = samples.Length * 2;
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);              // PCM
        w.Write((short)1);              // mono
        w.Write(SampleRate);
        w.Write(SampleRate * 2);        // bytes per second
        w.Write((short)2);              // block align
        w.Write((short)16);             // bits per sample
        w.Write("data"u8);
        w.Write(dataBytes);
        foreach (var s in samples)
            w.Write((short)Math.Clamp(s * short.MaxValue, short.MinValue, short.MaxValue));
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Developer helper: writes each jingle as a .wav file.</summary>
    public static void Export(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var sound in new[] { ToastSound.ChipBlip, ToastSound.ChipArpeggio, ToastSound.ChipBounce, ToastSound.ChipAlert, ToastSound.ChipFanfare })
            File.WriteAllBytes(Path.Combine(dir, $"{sound}.wav"), Wav(Synthesize(sound)));
    }
}
