using System.Runtime.InteropServices;

namespace ZendeskWatcher.UI;

/// <summary>
/// The app's own volume (Look &amp; sound → Volume): the same as its slider in Windows' Volume mixer, so it applies to
/// every sound the app plays (Windows sounds, chiptunes, songs) and never to other apps.
/// </summary>
public static class SoundVolume
{
    private static int? _applied;

    /// <summary>Sets the app's volume in percent (0-100); does nothing when it's already that.</summary>
    public static void Apply(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (_applied == percent)
            return;
        try
        {
            var level = (uint)(percent * 0xFFFF / 100);
            if (waveOutSetVolume(IntPtr.Zero, level | level << 16) == 0) // both channels
                _applied = percent;
        }
        catch (Exception ex)
        {
            Log.Error("Could not set the sound volume", ex);
        }
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutSetVolume(IntPtr device, uint volume);
}
