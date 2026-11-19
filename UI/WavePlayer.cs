using System.Runtime.InteropServices;

namespace ZendeskWatcher.UI;

/// <summary>
/// Plays 16-bit mono PCM through Windows' basic audio output (winmm), one sound at a time, with a quick fade-out.
/// Much lighter than Windows' media player: nothing stays loaded between sounds, and the audio device is closed as soon
/// as a sound ends. Call on the UI thread.
/// </summary>
internal static class WavePlayer
{
    private const int FadeMs = 300;
    // How far ahead of the reported position the fade starts: the part already on its way to the speakers.
    private const int LeadMs = 60;

    private static IntPtr _device, _header;
    private static byte[] _buffer = [];
    private static GCHandle _pin;
    private static int _dataBytes, _sampleRate;
    private static System.Windows.Forms.Timer? _watch;
    private static DateTime _stopAt = DateTime.MaxValue;
    private static bool _failedOnce;

    public static bool IsPlaying => _device != IntPtr.Zero && !IsDone();

    /// <summary>Plays the samples (16-bit mono PCM), stopping whatever was playing.</summary>
    public static void Play(ReadOnlySpan<byte> pcm, int sampleRate)
    {
        Close();
        var format = new WaveFormat
        {
            FormatTag = 1, Channels = 1, SamplesPerSec = (uint)sampleRate, AvgBytesPerSec = (uint)sampleRate * 2,
            BlockAlign = 2, BitsPerSample = 16
        };
        var result = waveOutOpen(out _device, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, 0);
        if (result != 0)
        {
            _device = IntPtr.Zero;
            if (!_failedOnce) // e.g. no speakers or headphones: say it once, not for every sound
                Log.Info($"Sound: no audio output (winmm error {result})");
            _failedOnce = true;
            return;
        }

        _sampleRate = sampleRate;
        _dataBytes = pcm.Length & ~1;
        _buffer = pcm[.._dataBytes].ToArray();
        _pin = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
        _header = Marshal.AllocHGlobal(HeaderSize);
        Marshal.StructureToPtr(new WaveHeader { Data = _pin.AddrOfPinnedObject(), BufferLength = (uint)_dataBytes }, _header, false);
        if (waveOutPrepareHeader(_device, _header, HeaderSize) != 0 || waveOutWrite(_device, _header, HeaderSize) != 0)
        {
            Log.Info("Sound: could not start playing");
            Close();
            return;
        }
        _stopAt = DateTime.MaxValue;
        Watch();
    }

    /// <summary>Fades out what's playing over a moment (e.g. a song when its notification is clicked).</summary>
    public static void FadeOut()
    {
        if (!IsPlaying)
            return;
        var start = Math.Min(PositionBytes() + _sampleRate * 2 * LeadMs / 1000, _dataBytes) & ~1;
        var fadeBytes = _sampleRate * 2 * FadeMs / 1000;
        var samples = MemoryMarshal.Cast<byte, short>(Span()[start..]);
        var fadeSamples = fadeBytes / 2;
        for (var i = 0; i < samples.Length; i++)
            samples[i] = i < fadeSamples ? (short)(samples[i] * (1 - (double)i / fadeSamples)) : (short)0;
        // In case the audio driver had already taken more than that: stop for certain once the fade is over.
        _stopAt = DateTime.UtcNow.AddMilliseconds(LeadMs + FadeMs + 40);
    }

    private static void Watch()
    {
        if (_watch is null)
        {
            _watch = new System.Windows.Forms.Timer { Interval = 100 };
            _watch.Tick += (_, _) =>
            {
                if (_device == IntPtr.Zero || IsDone() || DateTime.UtcNow >= _stopAt)
                    Close();
            };
        }
        _watch.Start();
    }

    /// <summary>Stops, closes the audio device and frees the buffer.</summary>
    private static void Close()
    {
        _watch?.Stop();
        if (_device != IntPtr.Zero)
        {
            waveOutReset(_device);
            if (_header != IntPtr.Zero)
                waveOutUnprepareHeader(_device, _header, HeaderSize);
            waveOutClose(_device);
            _device = IntPtr.Zero;
        }
        if (_header != IntPtr.Zero)
            Marshal.FreeHGlobal(_header);
        if (_pin.IsAllocated)
            _pin.Free();
        _header = IntPtr.Zero;
        _buffer = [];
        _dataBytes = 0;
    }

    private static Span<byte> Span() => _buffer.AsSpan(0, _dataBytes);

    private static bool IsDone() =>
        _header == IntPtr.Zero ||
        ((uint)Marshal.ReadInt32(_header, FlagsOffset) & WhdrDone) != 0;

    private static int PositionBytes()
    {
        var time = new MmTime { Type = TimeBytes };
        return waveOutGetPosition(_device, ref time, Marshal.SizeOf<MmTime>()) == 0 && time.Type == TimeBytes
            ? (int)Math.Min(time.Value, (uint)_dataBytes)
            : 0;
    }

    // ------------------------------------------------------------------ winmm

    private const uint WaveMapper = unchecked((uint)-1);
    private const uint WhdrDone = 1;
    private const uint TimeBytes = 4;
    private static readonly int HeaderSize = Marshal.SizeOf<WaveHeader>();
    private static readonly int FlagsOffset = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Flags));

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSec, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength, BytesRecorded;
        public IntPtr User;
        public uint Flags, Loops;
        public IntPtr Next, Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 12)]
    private struct MmTime
    {
        public uint Type;
        public uint Value;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr device, uint deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr device);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr device);

    [DllImport("winmm.dll")]
    private static extern int waveOutGetPosition(IntPtr device, ref MmTime time, int size);
}
