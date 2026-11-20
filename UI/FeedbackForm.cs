using ZendeskWatcher.SettingsPages;

namespace ZendeskWatcher.UI;

/// <summary>
/// "Send feedback": what happened or what you'd like, and whether to attach the logs. Sent by email from your own
/// Outlook (see <see cref="FeedbackMail"/>), so nothing else is needed.
/// </summary>
public sealed class FeedbackForm : Form
{
    private readonly TextBox _message;
    private readonly CheckBox _attach = Kit.Check("Attach the logs (recommended for problems)");

    public string Message => _message.Text.Trim();
    public bool AttachLogs => _attach.Checked;

    public FeedbackForm()
    {
        Text = "Zendesk Watcher – Send feedback";
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        Icon = AppIcon.CreateIcon(AppIcon.NeonGreen, 32);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Surface;
        Font = Theme.Body;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(0, 0, 24, 18);

        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Theme.Surface, Margin = Padding.Empty,
            Location = new Point(24, 20)
        };
        var heading = Theme.Label("Send feedback", Theme.CardTitle, Theme.Text, 440);
        heading.Margin = new Padding(0, 0, 0, 6);
        stack.Controls.Add(heading);
        var hint = Theme.Label("A problem, an idea, or something that's unclear. It's sent by email from your Outlook to the " +
                               "maintainer, so you'll find it in your Sent Items.", Theme.Small, Theme.TextMuted, 440);
        hint.Margin = new Padding(0, 0, 0, 10);
        stack.Controls.Add(hint);

        // The message box with the app's thin scrollbar beside it instead of Windows' own (unthemed) one.
        var bar = new ThinScrollBar { BackColor = Theme.Surface };
        _message = Theme.TextBox(440 - 14, "What happened, or what would you like?");
        _message.Multiline = true;
        _message.AcceptsReturn = true;
        _message.ScrollBars = ScrollBars.None;
        // In the theme's colors, with the border drawn around it like the app's other input boxes (Windows' own is
        // white and grey whatever the theme).
        _message.BorderStyle = BorderStyle.None;
        _message.BackColor = Theme.Surface;
        _message.ForeColor = Theme.Text;
        _message.Height = 140;
        _message.Margin = Padding.Empty;
        var messageHost = new Panel { Size = new Size(440, 140), BackColor = Theme.Surface, Margin = new Padding(0, 0, 0, 10) };
        _message.Location = Point.Empty;
        bar.Bounds = new Rectangle(440 - 10, 0, 10, 140);
        messageHost.Controls.Add(_message);
        messageHost.Controls.Add(bar);
        void LayoutMessage()
        {
            var sc = messageHost.DeviceDpi / 96f;
            var w = (int)(10 * sc);
            var pad = (int)(8 * sc);
            bar.Bounds = new Rectangle(messageHost.Width - w - (int)(3 * sc), (int)(4 * sc), w, messageHost.Height - (int)(8 * sc));
            _message.Bounds = new Rectangle(pad, (int)(6 * sc), bar.Left - pad - (int)(4 * sc), messageHost.Height - (int)(12 * sc));
        }
        messageHost.Resize += (_, _) => LayoutMessage();
        LayoutMessage();
        // The border: the theme's, in the accent color while typing (like the filter boxes).
        messageHost.Paint += (_, e) =>
        {
            var sc = messageHost.DeviceDpi / 96f;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = Theme.RoundedRect(new RectangleF(0.5f, 0.5f, messageHost.Width - 1.5f, messageHost.Height - 1.5f), 4 * sc);
            using var pen = new Pen(_message.Focused ? Theme.Accent : Theme.Border, _message.Focused ? 1.5f : 1f);
            e.Graphics.DrawPath(pen, path);
        };
        _message.GotFocus += (_, _) => messageHost.Invalidate();
        _message.LostFocus += (_, _) => messageHost.Invalidate();
        bar.State = () =>
        {
            if (!_message.IsHandleCreated)
                return null;
            var lines = (int)SendMessage(_message.Handle, 0x00BA /* EM_GETLINECOUNT */, 0, 0);
            var first = (int)SendMessage(_message.Handle, 0x00CE /* EM_GETFIRSTVISIBLELINE */, 0, 0);
            var page = Math.Max(1, _message.ClientSize.Height / Math.Max(1, TextRenderer.MeasureText("Ag", _message.Font).Height));
            return lines <= page ? null : (0, lines - 1, page, first);
        };
        bar.ScrollTo = line =>
        {
            var first = (int)SendMessage(_message.Handle, 0x00CE /* EM_GETFIRSTVISIBLELINE */, 0, 0);
            SendMessage(_message.Handle, 0x00B6 /* EM_LINESCROLL */, 0, line - first);
            bar.Invalidate();
        };
        // The box scrolls by typing, the arrow keys and the wheel (three lines a notch, done here: no Windows scrollbar).
        var wheel = 0; // touchpads send small steps (e.g. 30 of a notch's 120): added up until they make a line
        _message.MouseWheel += (_, e) =>
        {
            if (e is HandledMouseEventArgs handled)
                handled.Handled = true;
            wheel += e.Delta * 3;
            var lines = wheel / 120;
            wheel -= lines * 120;
            if (lines != 0)
                SendMessage(_message.Handle, 0x00B6 /* EM_LINESCROLL */, 0, -lines);
            bar.Invalidate();
        };
        _message.TextChanged += (_, _) => bar.Invalidate();
        _message.KeyUp += (_, _) => bar.Invalidate();
        _message.MouseUp += (_, _) => bar.Invalidate();
        stack.Controls.Add(messageHost);

        _attach.Checked = true;
        _attach.Margin = new Padding(0, 0, 0, 2);
        stack.Controls.Add(_attach);
        var what = Theme.Label("The log, your settings and app details: ticket numbers and subjects, but no comments or client messages.", Theme.Small, Theme.TextMuted, 440);
        what.Margin = new Padding(22, 0, 0, 16);
        stack.Controls.Add(what);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true, BackColor = Theme.Surface,
            MinimumSize = new Size(440, 0), Margin = Padding.Empty
        };
        var send = Theme.PrimaryButton("Send");
        var cancel = Theme.SecondaryButton("Cancel");
        send.Margin = new Padding(8, 0, 0, 0);
        cancel.Margin = new Padding(8, 0, 0, 0);
        send.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        buttons.Controls.Add(send);
        buttons.Controls.Add(cancel);
        stack.Controls.Add(buttons);
        Controls.Add(stack);
        CancelButton = cancel; // no AcceptButton: Enter makes a new line in the message

        // Send only with something to send.
        send.Enabled = false;
        _message.TextChanged += (_, _) => send.Enabled = Message.Length > 0;

        Load += (_, _) => Theme.ApplyWindowChrome(this, Theme.Current.Dark);
        Shown += (_, _) => _message.Focus();
    }

    /// <summary>For the rendered screenshots: a message in the box.</summary>
    internal void SampleText(string text) => _message.Text = text;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, int wParam, int lParam);
}
