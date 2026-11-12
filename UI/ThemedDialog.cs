namespace ZendeskWatcher.UI;

/// <summary>
/// A question or message in the current theme (colors, button shape, dark title bar), instead of the grey Windows
/// message box. Buttons say what they do ("Save", "Don't save") rather than Yes / No.
/// </summary>
public sealed class ThemedDialog : Form
{
    public enum Kind { Question, Info, Error }

    /// <summary>A button: its text, what it returns, and whether it's the main (accent) one.</summary>
    public sealed record Choice(string Label, DialogResult Result, bool Primary = false);

    private ThemedDialog(string title, string message, Kind kind, IReadOnlyList<Choice> choices)
    {
        Text = "Zendesk Watcher";
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Surface;
        Font = Theme.Body;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(0, 0, 24, 18); // left and top: the stack's position

        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Theme.Surface, Margin = Padding.Empty,
            Location = new Point(24, 20)
        };
        var heading = Theme.Label(title, Theme.CardTitle, kind == Kind.Error ? Theme.Danger : Theme.Text, 400);
        heading.Margin = new Padding(0, 0, 0, 8);
        stack.Controls.Add(heading);
        var body = Theme.Label(message, Theme.Body, Theme.TextMuted, 400);
        body.Margin = new Padding(0, 0, 0, 18);
        stack.Controls.Add(body);

        // Buttons on the right, the main one first (leftmost), like the Settings footer and Windows dialogs.
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true, BackColor = Theme.Surface,
            MinimumSize = new Size(400, 0), Margin = Padding.Empty
        };
        foreach (var choice in choices.Reverse())
        {
            var button = choice.Primary ? Theme.PrimaryButton(choice.Label) : Theme.SecondaryButton(choice.Label);
            button.Margin = new Padding(8, 0, 0, 0);
            button.Click += (_, _) => { DialogResult = choice.Result; Close(); };
            buttons.Controls.Add(button);
            if (choice.Primary)
                AcceptButton = button;
            if (choice.Result == DialogResult.Cancel || (choices.Count == 1))
                CancelButton = button;
        }
        stack.Controls.Add(buttons);
        Controls.Add(stack);

        Load += (_, _) => Theme.ApplyWindowChrome(this, Theme.Current.Dark);
        Shown += (_, _) => (AcceptButton as Control ?? buttons.Controls[0]).Focus();
    }

    /// <summary>Shows the dialog over <paramref name="owner"/> and returns the chosen button's result (Cancel if closed).</summary>
    public static DialogResult Show(IWin32Window? owner, string title, string message, Kind kind, params Choice[] choices)
    {
        using var dialog = new ThemedDialog(title, message, kind, choices);
        var result = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return result == DialogResult.None ? DialogResult.Cancel : result;
    }

    /// <summary>A yes/no question: true for <paramref name="confirm"/> (the main button), false for Cancel or closing.</summary>
    public static bool Confirm(IWin32Window? owner, string title, string message, string confirm) =>
        Show(owner, title, message, Kind.Question, new Choice(confirm, DialogResult.OK, Primary: true), new Choice("Cancel", DialogResult.Cancel)) == DialogResult.OK;

    /// <summary>Developer helper: the "Save your changes?" dialog in every appearance, as PNG files.</summary>
    internal static void RenderSamples(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var theme in Enum.GetValues<NotificationTheme>())
        {
            Theme.Use(theme);
            using var dialog = new ThemedDialog("Save your changes?", "You have changes that aren't saved yet.", Kind.Question,
                [new Choice("Save", DialogResult.Yes, Primary: true), new Choice("Don't save", DialogResult.No), new Choice("Cancel", DialogResult.Cancel)]);
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(-5000, -5000);
            dialog.Show();
            Application.DoEvents();
            using var bitmap = new Bitmap(dialog.Width, dialog.Height);
            dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(dir, $"dialog-{theme}.png"));
        }
    }

    /// <summary>A message with an OK button.</summary>
    public static void Inform(IWin32Window? owner, string title, string message, Kind kind = Kind.Info) =>
        Show(owner, title, message, kind, new Choice("OK", DialogResult.OK, Primary: true));
}
