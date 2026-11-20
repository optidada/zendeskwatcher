using System.Runtime.InteropServices;
using ZendeskWatcher.Tracking;

namespace ZendeskWatcher.UI.Popups;

/// <summary>
/// Once a second: was there any keyboard or mouse input (Windows' last-input time moved)? Feeds <see cref="BusyRule"/>.
/// Nothing about what was typed or clicked is read. Every 10 minutes the log gets the input seconds per minute and how
/// many notifications waited, to tune the numbers.
/// </summary>
internal sealed class BusyWatch
{
    private static readonly TimeSpan ReportEvery = TimeSpan.FromMinutes(10);

    private readonly BusyRule _rule = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private uint _lastInput;
    private DateTime _busySince;
    private DateTime _reportStart = DateTime.UtcNow;
    private readonly List<int> _perMinute = [];
    private int _thisMinute;
    private int _secondsInMinute;
    private TimeSpan _busyInPeriod;
    private int _held, _releasedCalm, _releasedMax;
    private TimeSpan _heldTotal;

    public BusyWatch()
    {
        _lastInput = LastInput();
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public bool Busy => _rule.Busy && !PopupForm.AssumeUserPresent;

    public void NoteHeld(NotificationContent content)
    {
        _held++;
        Log.Info($"Busy: held back ({_rule.ActiveSeconds} of the last 60 s with input): {content.LogName}");
    }

    public void NoteReleased(NotificationContent content, TimeSpan waited, bool calm)
    {
        if (calm)
            _releasedCalm++;
        else
            _releasedMax++;
        _heldTotal += waited;
        Log.Info($"Busy: shown after waiting {Duration(waited)} ({(calm ? "you calmed down" : "longest wait reached")}): {content.LogName}");
    }

    private void Tick()
    {
        try
        {
            var now = DateTime.UtcNow;
            var input = LastInput();
            var active = input != _lastInput && !PopupForm.UserAwayFor(TimeSpan.FromSeconds(2));
            _lastInput = input;

            if (_rule.Add(active, now))
            {
                if (_rule.Busy)
                {
                    _busySince = now;
                    Log.Info($"Busy: started ({_rule.ActiveSeconds} of the last 60 s with input)");
                }
                else
                {
                    _busyInPeriod += now - Max(_busySince, _reportStart);
                    Log.Info($"Busy: ended after {Duration(now - _busySince)}");
                }
            }

            if (active)
                _thisMinute++;
            if (++_secondsInMinute >= 60)
            {
                _perMinute.Add(_thisMinute);
                _thisMinute = 0;
                _secondsInMinute = 0;
            }
            if (now - _reportStart >= ReportEvery)
                Report(now);
        }
        catch (Exception ex)
        {
            _timer.Stop();
            Log.Error("Busy check stopped", ex);
        }
    }

    private void Report(DateTime now)
    {
        var busy = _busyInPeriod + (_rule.Busy ? now - Max(_busySince, _reportStart) : TimeSpan.Zero);
        Log.Info($"Busy: last {ReportEvery.TotalMinutes:0} min: busy {Duration(busy)}; input seconds per minute: " +
                 $"{string.Join(" ", _perMinute)}; held {_held}, shown when calm {_releasedCalm}, after longest wait {_releasedMax}" +
                 (_releasedCalm + _releasedMax > 0 ? $", average wait {Duration(_heldTotal / (_releasedCalm + _releasedMax))}" : ""));
        _reportStart = now;
        _perMinute.Clear();
        _busyInPeriod = TimeSpan.Zero;
        _held = _releasedCalm = _releasedMax = 0;
        _heldTotal = TimeSpan.Zero;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static string Duration(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{(int)t.TotalSeconds} s";

    private static uint LastInput()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? info.dwTime : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
}
