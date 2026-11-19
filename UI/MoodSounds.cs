namespace ZendeskWatcher.UI;

/// <summary>
/// The "mood" sounds, synthesized like the chiptunes but softer and quieter, each 3½-5 s: dark americana (plucked
/// banjo and guitar, a slide, a low drone; original tunes, no vocals) and nature (a brook, wind chimes, a distant bird).
/// Played through <see cref="ChipSounds"/>.
/// </summary>
public static class MoodSounds
{
    private const int Rate = 22050;

    /// <summary>The sounds this class makes: dark americana and nature.</summary>
    public static bool Handles(ToastSound sound) => sound >= ToastSound.AmericanaBanjoRoad;

    /// <summary>The sound, normalized: softer than the chiptunes (0.3), the nature ones softer still.</summary>
    public static float[] Render(ToastSound sound) => sound switch
    {
        ToastSound.AmericanaBanjoRoad => Finish(BanjoRoad(), 0.22),
        ToastSound.AmericanaSlide => Finish(Slide(), 0.22),
        ToastSound.AmericanaHollow => Finish(Hollow(), 0.22),
        ToastSound.NatureStream => Finish(Stream(), 0.16),
        ToastSound.NatureWindChimes => Finish(WindChimes(), 0.14),
        ToastSound.NatureBirdsong => Finish(Birdsong(), 0.13),
        _ => []
    };

    // ------------------------------------------------------------------ americana

    /// <summary>A short banjo phrase in A minor (E D C, then A rings out) over a soft A drone.</summary>
    private static double[] BanjoRoad()
    {
        const double beat = 60.0 / 84;
        var mix = new double[Seconds(3.8)];
        Drone(mix, 0, 3.8, Hz(45), 0.09);
        (double Beats, int Midi, double Level)[] phrase = [(0, 64, 0.85), (0.5, 62, 0.7), (1, 60, 0.8), (1.5, 62, 0.6), (2, 57, 1.0)];
        foreach (var (beats, midi, level) in phrase)
            Add(mix, beats * beat, Pluck(Hz(midi), beats < 2 ? 1.2 : 2.2, bright: 0.75, sustain: beats < 2 ? 0.990 : 0.994), level);
        Add(mix, 2 * beat, Pluck(Hz(45), 2.2, bright: 0.35, sustain: 0.996), 0.6); // low A under the last note
        return Reverb(mix, 0.2);
    }

    /// <summary>A low D, a slide guitar gliding from A3 up to C4, then settling back to A3, over a D drone.</summary>
    private static double[] Slide()
    {
        var mix = new double[Seconds(4.2)];
        Drone(mix, 0, 4.2, Hz(38), 0.11);
        Add(mix, 0.00, Pluck(Hz(50), 3.0, bright: 0.45, sustain: 0.997), 0.85);
        Add(mix, 0.35, Pluck(Hz(57), 1.9, bright: 0.5, sustain: 0.997, glideTo: Hz(60), glideStart: 0.25, glideTime: 0.4, vibrato: 0.006), 0.85);
        Add(mix, 2.2, Pluck(Hz(60), 2.0, bright: 0.5, sustain: 0.997, glideTo: Hz(57), glideStart: 0.15, glideTime: 0.5, vibrato: 0.005), 0.75);
        return Reverb(mix, 0.25);
    }

    /// <summary>Slow fingerpicking in E minor (Em – C – Em) over a drone, with a few banjo notes on top.</summary>
    private static double[] Hollow()
    {
        const double beat = 60.0 / 76;
        var eighth = beat / 2;
        var mix = new double[Seconds(5.6)];
        Drone(mix, 0, 5.6, Hz(40), 0.08);
        (int Bass, int[] Upper)[] bars = [(40, [59, 64, 67]), (48, [60, 64, 67]), (40, [59, 64, 67])]; // Em, C, Em
        for (var bar = 0; bar < bars.Length; bar++)
        {
            var start = bar * 4 * beat;
            var (bass, upper) = bars[bar];
            Add(mix, start, Pluck(Hz(bass), bar < 2 ? 2.6 : 2.4, bright: 0.35, sustain: 0.996), 0.9);
            if (bar < 2)
            {
                int[] pattern = [0, 1, 2, 1, 0, 1, 2]; // up and back down
                for (var i = 0; i < pattern.Length; i++)
                    Add(mix, start + (i + 1) * eighth, Pluck(Hz(upper[pattern[i]]), 1.4, bright: 0.45, sustain: 0.993), 0.45);
            }
            else
                for (var i = 0; i < upper.Length; i++) // the last chord, strummed slowly
                    Add(mix, start + 0.04 * i, Pluck(Hz(upper[i]), 2.3, bright: 0.4, sustain: 0.996), 0.4);
        }
        (double At, int Midi)[] banjo = [(4 * beat + eighth, 71), (5 * beat, 69), (6 * beat, 67), (7 * beat, 66)];
        foreach (var (at, midi) in banjo)
            Add(mix, at, Pluck(Hz(midi), 1.4, bright: 0.75, sustain: 0.991), 0.5);
        return Reverb(mix, 0.3);
    }

    // ------------------------------------------------------------------ nature

    /// <summary>A quiet brook: soft running water, and drops at different pitches, a little irregular.</summary>
    private static double[] Stream()
    {
        var mix = new double[Seconds(4.2)];
        Breeze(mix, 0, 4.2, 0.10, brightness: 0.12, seed: 3); // the water's soft rush
        (double At, double Freq, double Level)[] drops = [(0.15, 880, 1.0), (0.55, 1180, 0.7), (0.9, 760, 0.85), (1.6, 1020, 0.8), (2.05, 1320, 0.55), (2.6, 840, 0.75), (3.3, 1100, 0.6)];
        foreach (var (at, freq, level) in drops)
            Drop(mix, at, freq, level);
        return Reverb(mix, 0.28);
    }

    /// <summary>Wind chimes in a soft breeze: five bell tones (inharmonic partials) from a calm pentatonic set.</summary>
    private static double[] WindChimes()
    {
        var mix = new double[Seconds(4.5)];
        Breeze(mix, 0, 4.5, 0.07, brightness: 0.05, seed: 5);
        (double At, int Midi, double Level)[] chimes = [(0.1, 81, 0.9), (0.35, 88, 0.7), (0.8, 84, 0.8), (1.6, 86, 0.6), (2.1, 81, 0.75)]; // A5 E6 C6 D6 A5
        foreach (var (at, midi, level) in chimes)
        {
            var offset = Seconds(at);
            var f = Hz(midi);
            for (var i = 0; offset + i < mix.Length; i++)
            {
                var t = (double)i / Rate;
                var tone = Math.Sin(2 * Math.PI * f * t) * Math.Exp(-t * 1.8)
                         + 0.35 * Math.Sin(2 * Math.PI * f * 2.76 * t) * Math.Exp(-t * 4.5)
                         + 0.12 * Math.Sin(2 * Math.PI * f * 5.40 * t) * Math.Exp(-t * 8);
                mix[offset + i] += tone * Math.Min(1, t / 0.004) * level * 0.6;
            }
        }
        return Reverb(mix, 0.3);
    }

    /// <summary>A distant bird: a few soft whistled calls (quick pitch sweeps), with a faint breeze.</summary>
    private static double[] Birdsong()
    {
        var mix = new double[Seconds(3.8)];
        Breeze(mix, 0, 3.8, 0.05, brightness: 0.05, seed: 9);
        // (start, length, from Hz, to Hz, level): a call of two notes, then an answer of three.
        (double At, double Length, double From, double To, double Level)[] calls =
        [
            (0.20, 0.16, 2600, 3300, 0.9), (0.42, 0.22, 3300, 2500, 0.8),
            (1.60, 0.10, 2800, 3400, 0.6), (1.75, 0.10, 2800, 3400, 0.6), (1.92, 0.25, 3400, 2400, 0.7),
            (3.00, 0.18, 2500, 3100, 0.5)
        ];
        foreach (var (at, length, from, to, level) in calls)
        {
            var offset = Seconds(at);
            var phase = 0.0;
            for (var i = 0; i < Seconds(length) && offset + i < mix.Length; i++)
            {
                var t = (double)i / Seconds(length);
                var f = from + (to - from) * Math.Sin(t * Math.PI / 2) + 60 * Math.Sin(2 * Math.PI * 28 * t * length); // a slight warble
                phase += 2 * Math.PI * f / Rate;
                var envelope = Math.Sin(Math.PI * t); // soft in, soft out
                mix[offset + i] += Math.Sin(phase) * envelope * envelope * level;
            }
        }
        return Reverb(mix, 0.35);
    }

    /// <summary>A water drop: a sine whose pitch rises quickly as the bubble closes.</summary>
    private static void Drop(double[] mix, double at, double freq, double level)
    {
        var offset = Seconds(at);
        var phase = 0.0;
        for (var i = 0; offset + i < mix.Length && i < Seconds(0.12); i++)
        {
            var t = (double)i / Rate;
            phase += 2 * Math.PI * freq * (1 + 6 * t) / Rate;
            mix[offset + i] += Math.Sin(phase) * Math.Min(1, t / 0.002) * Math.Exp(-t * 38) * level;
        }
    }

    /// <summary>Soft filtered noise that swells and fades slowly: running water or a breeze in the background.</summary>
    private static void Breeze(double[] mix, double at, double seconds, double level, double brightness, int seed)
    {
        var random = new Random(seed);
        var offset = Seconds(at);
        var low = 0.0;
        var lower = 0.0;
        for (var i = 0; i < Seconds(seconds) && offset + i < mix.Length; i++)
        {
            var t = (double)i / Rate;
            low += brightness * ((random.NextDouble() * 2 - 1) - low);
            lower += brightness * (low - lower);
            var swell = Math.Min(1, t / 0.8) * Math.Min(1, (seconds - t) / 0.9) * (0.75 + 0.25 * Math.Sin(2 * Math.PI * 0.4 * t));
            mix[offset + i] += lower * swell * level * 6;
        }
    }

    // ------------------------------------------------------------------ building blocks

    private static double Hz(int midi) => 440 * Math.Pow(2, (midi - 69) / 12.0);
    private static int Seconds(double s) => (int)(s * Rate);

    private static void Add(double[] mix, double at, double[] sound, double level)
    {
        var offset = Seconds(at);
        for (var i = 0; i < sound.Length && offset + i < mix.Length; i++)
            mix[offset + i] += sound[i] * level;
    }

    /// <summary>
    /// A plucked string (Karplus-Strong): a burst of noise circulating in a delay line one period long, softened a
    /// little on each pass. Bright = more high notes in the pluck (banjo) or fewer (guitar); sustain = how long it rings.
    /// Optional slide: the pitch glides to <paramref name="glideTo"/>, then a slow vibrato.
    /// </summary>
    private static double[] Pluck(double freq, double seconds, double bright, double sustain,
        double glideTo = 0, double glideStart = 0, double glideTime = 0, double vibrato = 0)
    {
        var output = new double[Seconds(seconds)];
        var line = new double[4096];
        var random = new Random((int)(freq * 100));
        var period = Rate / freq;
        var low = 0.0;
        for (var i = 0; i < (int)period; i++)
        {
            low += bright * ((random.NextDouble() * 2 - 1) - low); // darker noise = warmer string
            line[i] = low;
        }
        var write = (int)period;
        for (var n = 0; n < output.Length; n++)
        {
            var t = (double)n / Rate;
            var f = freq;
            if (glideTo > 0 && t > glideStart)
            {
                var k = Math.Min(1, (t - glideStart) / glideTime);
                f = freq * Math.Pow(glideTo / freq, k * k * (3 - 2 * k)); // smooth glide
                if (k >= 1)
                    f *= 1 + vibrato * Math.Sin(2 * Math.PI * 5.2 * (t - glideStart - glideTime));
            }
            var delay = Rate / f;
            var read = write - delay;
            var i0 = (int)Math.Floor(read);
            var frac = read - i0;
            var a = line[((i0 % line.Length) + line.Length) % line.Length];
            var b = line[(((i0 - 1) % line.Length) + line.Length) % line.Length];
            var c = line[(((i0 + 1) % line.Length) + line.Length) % line.Length];
            var delayed = a * (1 - frac) + c * frac;
            var y = sustain * 0.5 * (delayed + (a * frac + b * (1 - frac)));
            line[write % line.Length] = y;
            write++;
            output[n] = y;
        }
        // Soft end, no click.
        var fade = Math.Min(output.Length, Seconds(0.08));
        for (var i = 0; i < fade; i++)
            output[output.Length - 1 - i] *= (double)i / fade;
        return output;
    }

    /// <summary>A soft low drone like a harmonium: a few harmonics, two slightly detuned, fading in and out.</summary>
    private static void Drone(double[] mix, double at, double seconds, double freq, double level)
    {
        var offset = Seconds(at);
        var length = Seconds(seconds);
        for (var i = 0; i < length && offset + i < mix.Length; i++)
        {
            var t = (double)i / Rate;
            var envelope = Math.Min(1, t / 0.35) * Math.Min(1, (seconds - t) / 0.5);
            var tone = Math.Sin(2 * Math.PI * freq * t) + Math.Sin(2 * Math.PI * freq * 1.003 * t)
                     + 0.35 * Math.Sin(2 * Math.PI * freq * 2 * t) + 0.12 * Math.Sin(2 * Math.PI * freq * 3 * t);
            mix[offset + i] += tone * envelope * level * 0.5;
        }
    }

    /// <summary>A small room: a few feedback echoes at odd lengths (Schroeder-style), mixed in at <paramref name="wet"/>.</summary>
    private static double[] Reverb(double[] dry, double wet)
    {
        var output = new double[dry.Length + Seconds(0.4)];
        Array.Copy(dry, output, dry.Length);
        var room = new double[output.Length];
        foreach (var (delayMs, feedback) in new[] { (29.7, 0.72), (37.1, 0.70), (41.1, 0.68), (43.7, 0.66) })
        {
            var delay = (int)(delayMs / 1000 * Rate);
            var comb = new double[output.Length];
            for (var i = 0; i < output.Length; i++)
                comb[i] = (i < dry.Length ? dry[i] : 0) + (i >= delay ? comb[i - delay] * feedback : 0);
            for (var i = 0; i < output.Length; i++)
                room[i] += comb[i] / 4;
        }
        var low = 0.0;
        for (var i = 0; i < output.Length; i++)
        {
            low += 0.3 * (room[i] - low); // a darker, softer room
            output[i] += low * wet;
        }
        return output;
    }

    /// <summary>Normalizes to <paramref name="level"/> (the chiptunes use 0.3; these are softer) with a short fade at the end.</summary>
    private static float[] Finish(double[] mix, double level)
    {
        var peak = Math.Max(1e-6, mix.Max(Math.Abs));
        var fade = Math.Min(mix.Length, Seconds(0.15));
        var output = new float[mix.Length];
        for (var i = 0; i < mix.Length; i++)
            output[i] = (float)(mix[i] / peak * level * Math.Min(1.0, (double)(mix.Length - i) / fade));
        return output;
    }
}
