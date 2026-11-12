using System.Media;

namespace ZendeskWatcher.UI;

/// <summary>
/// Chiptune jingles and short songs in the spirit of Amiga tracker music, synthesized in code: pulse-wave melody and
/// bass, noise drums, tracker-style arpeggios and a soft low-pass like the Amiga's audio filter. All original little
/// tunes, not taken from any existing game or song. Windows toasts can't play custom sounds for an unpackaged app,
/// so these are played by the app itself.
/// </summary>
public static class ChipSounds
{
    private const int SampleRate = 22050;
    private static readonly Dictionary<ToastSound, byte[]> Cache = [];
    private static SoundPlayer? _player; // kept so it isn't collected while playing

    public static bool IsChip(ToastSound sound) => sound >= ToastSound.ChipBlip;

    public static void Play(ToastSound sound)
    {
        try
        {
            if (!Cache.TryGetValue(sound, out var wav))
                Cache[sound] = wav = Wav(Render(sound));
            _player?.Stop();
            _player?.Dispose();
            _player = new SoundPlayer(new MemoryStream(wav));
            _player.Play();
        }
        catch (Exception ex)
        {
            Log.Error("Could not play chiptune sound", ex);
        }
    }

    // ------------------------------------------------------------------ notes and songs

    /// <summary>One note: MIDI number (0 = rest), length, pulse width, optional arpeggio offsets (semitones).</summary>
    private readonly record struct Note(int Midi, int Ms, double Duty = 0.25, int[]? Arp = null, double Decay = 3.0);

    /// <summary>A voice of a song: notes plus a mix level.</summary>
    private sealed record Voice(Note[] Notes, double Level);

    /// <summary>Notes on a 16th-note grid: (midi, steps). Melody notes are slightly detached, like a tracker.</summary>
    private static Note[] Steps(int bpm, double duty, double decay, params (int Midi, int Steps)[] notes)
    {
        var step = 60000 / bpm / 4;
        return notes.Select(n => new Note(n.Midi, n.Steps * step, duty, null, decay)).ToArray();
    }

    private static Note[] Jingle(ToastSound sound) => sound switch
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

    /// <summary>The songs: melody, bass and a drum pattern (k = kick, s = snare, h = hi-hat, . = nothing, per 16th).</summary>
    private static (Voice[] Voices, int Bpm, string Drums)? Song(ToastSound sound) => sound switch
    {
        // Cheerful major-key hop (C – Am – F – G – C) over a pumping octave bass.
        ToastSound.SongBouncy => ([
            new Voice(Steps(140, 0.25, 2.2,
                (76, 2), (79, 2), (84, 2), (79, 2),   (81, 2), (77, 2), (81, 2), (84, 2),
                (83, 2), (79, 2), (86, 2), (83, 2),   (84, 1), (86, 1), (88, 2), (84, 4),
                (0, 1), (91, 1), (88, 1), (84, 1), (79, 2), (84, 6)), 1.0),
            new Voice(Steps(140, 0.5, 4.0,
                (48, 2), (60, 2), (48, 2), (60, 2),   (45, 2), (57, 2), (45, 2), (57, 2),
                (41, 2), (53, 2), (41, 2), (53, 2),   (43, 2), (55, 2), (43, 2), (55, 2),
                (48, 4), (36, 8)), 0.55)
          ], 140, "k.h.s.h.k.h.s.h.k.h.s.h.k.hks.hhk...s.......") ,

        // Funky minor riff with a syncopated bass (Am – G – Am – Em – Am).
        ToastSound.SongGroove => ([
            new Voice(Steps(124, 0.25, 2.5,
                (69, 1), (0, 1), (69, 1), (72, 1), (0, 1), (74, 1), (76, 2),
                (79, 1), (76, 1), (74, 1), (72, 1), (74, 2), (0, 2),
                (69, 1), (0, 1), (69, 1), (72, 1), (0, 1), (74, 1), (76, 2),
                (72, 1), (74, 1), (71, 2), (69, 4)), 1.0),
            new Voice(Steps(124, 0.5, 3.5,
                (45, 2), (57, 1), (45, 1), (0, 1), (45, 1), (57, 2),
                (43, 2), (55, 1), (43, 1), (0, 1), (43, 1), (55, 2),
                (45, 2), (57, 1), (45, 1), (0, 1), (45, 1), (57, 2),
                (40, 2), (52, 1), (40, 1), (45, 4)), 0.6),
            new Voice([new Note(0, 60000 / 124 / 4 * 30), new Note(81, 600, 0.5, [0, 3, 7], 2.0)], 0.7)
          ], 124, "k..hs.hhk.khs.h.k..hs.hhk.k.s...k"),

        // Triumphant fanfare (F – Bb – C – F) ending on a shimmering major chord.
        ToastSound.SongCelebration => ([
            new Voice(Steps(150, 0.25, 2.0,
                (77, 2), (77, 1), (77, 1), (81, 2), (84, 2),
                (82, 2), (81, 2), (79, 4),
                (81, 2), (81, 1), (81, 1), (84, 2), (89, 2),
                (88, 2), (91, 2)), 1.0),
            new Voice([new Note(0, 60000 / 150 / 4 * 32), new Note(89, 900, 0.5, [0, 4, 7], 1.6)], 1.0),
            new Voice(Steps(150, 0.5, 3.0,
                (41, 4), (53, 4), (46, 4), (58, 4), (48, 4), (60, 4), (53, 4), (41, 4),
                (41, 12)), 0.55)
          ], 150, "k.h.k.h.s.h.k.h.k.h.k.h.s.s.s.s.k...........")
        ,
        _ => null
    };

    // ------------------------------------------------------------------ synthesis

    private static float[] Render(ToastSound sound)
    {
        double[] mix;
        if (Song(sound) is { } song)
        {
            var length = song.Voices.Max(v => v.Notes.Sum(n => SampleRate * n.Ms / 1000));
            mix = new double[length + SampleRate / 4];
            foreach (var voice in song.Voices)
                AddVoice(mix, voice.Notes, voice.Level);
            AddDrums(mix, song.Drums, 60.0 / song.Bpm / 4);
        }
        else
        {
            var notes = Jingle(sound);
            mix = new double[notes.Sum(n => SampleRate * n.Ms / 1000)];
            AddVoice(mix, notes, 1.0);
        }

        // Soft one-pole low-pass (rounds off the square edges), then normalize to a comfortable level.
        var output = new float[mix.Length];
        var last = 0.0;
        var peak = 1e-6;
        for (var i = 0; i < mix.Length; i++)
        {
            last += 0.35 * (mix[i] - last);
            mix[i] = last;
            peak = Math.Max(peak, Math.Abs(last));
        }
        for (var i = 0; i < mix.Length; i++)
            output[i] = (float)(mix[i] / peak * 0.3);
        return output;
    }

    private static void AddVoice(double[] mix, Note[] notes, double level)
    {
        var position = 0;
        foreach (var note in notes)
        {
            var count = SampleRate * note.Ms / 1000;
            if (note.Midi > 0)
            {
                var phase = 0.0;
                for (var i = 0; i < count && position + i < mix.Length; i++)
                {
                    var t = (double)i / SampleRate;
                    // Tracker arpeggio: switch between the offsets every 20 ms (one "tick" at 50 Hz).
                    var offset = note.Arp is { Length: > 0 } arp ? arp[(int)(t / 0.02) % arp.Length] : 0;
                    var vibrato = note.Ms > 150 ? 0.15 * Math.Sin(2 * Math.PI * 6 * t) : 0;
                    var freq = 440 * Math.Pow(2, (note.Midi + offset + vibrato - 69) / 12.0);
                    phase = (phase + freq / SampleRate) % 1.0;

                    var pulse = phase < note.Duty ? 1.0 : -1.0;
                    var attack = Math.Min(1.0, t / 0.003);
                    var release = Math.Min(1.0, (count - i) / (SampleRate * 0.004)); // no clicks at the end
                    var decay = Math.Exp(-note.Decay * i / count);
                    mix[position + i] += pulse * attack * release * decay * level;
                }
            }
            position += count;
        }
    }

    private static void AddDrums(double[] mix, string pattern, double stepSeconds)
    {
        var rng = new Random(64);
        for (var step = 0; step < pattern.Length; step++)
        {
            var start = (int)(step * stepSeconds * SampleRate);
            switch (pattern[step])
            {
                case 'k': // kick: a quick downward sine sweep
                    for (int i = 0, n = (int)(0.09 * SampleRate); i < n && start + i < mix.Length; i++)
                    {
                        var t = (double)i / SampleRate;
                        var freq = 50 + 110 * Math.Exp(-t * 40);
                        mix[start + i] += Math.Sin(2 * Math.PI * freq * t) * Math.Exp(-t * 30) * 0.9;
                    }
                    break;
                case 's': // snare: noise with a little body
                    for (int i = 0, n = (int)(0.09 * SampleRate); i < n && start + i < mix.Length; i++)
                    {
                        var t = (double)i / SampleRate;
                        var noise = rng.NextDouble() * 2 - 1;
                        mix[start + i] += (noise * 0.6 + Math.Sin(2 * Math.PI * 190 * t) * 0.3) * Math.Exp(-t * 35) * 0.7;
                    }
                    break;
                case 'h': // hi-hat: short, bright noise
                    var previous = 0.0;
                    for (int i = 0, n = (int)(0.025 * SampleRate); i < n && start + i < mix.Length; i++)
                    {
                        var noise = rng.NextDouble() * 2 - 1;
                        var bright = noise - previous; // crude high-pass
                        previous = noise;
                        mix[start + i] += bright * Math.Exp(-(double)i / n * 5) * 0.18;
                    }
                    break;
            }
        }
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

    /// <summary>Developer helper: writes every jingle and song as a .wav file, with its length.</summary>
    public static void Export(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var sound in Enum.GetValues<ToastSound>().Where(IsChip))
        {
            var samples = Render(sound);
            File.WriteAllBytes(Path.Combine(dir, $"{sound}.wav"), Wav(samples));
            File.AppendAllText(Path.Combine(dir, "lengths.txt"), $"{sound}: {samples.Length / (double)SampleRate:0.0} s{Environment.NewLine}");
        }
    }
}
