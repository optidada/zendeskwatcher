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

        _message = Theme.TextBox(440, "What happened, or what would you like?");
        _message.Multiline = true;
        _message.AcceptsReturn = true;
        _message.ScrollBars = ScrollBars.Vertical;
        _message.Height = 140;
        _message.Margin = new Padding(0, 0, 0, 10);
        stack.Controls.Add(_message);

        _attach.Checked = true;
        _attach.Margin = new Padding(0, 0, 0, 2);
        stack.Controls.Add(_attach);
        var what = Theme.Label("The log, your settings and app details: ticket numbers and subjects, but no comments or customer messages.", Theme.Small, Theme.TextMuted, 440);
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
}
