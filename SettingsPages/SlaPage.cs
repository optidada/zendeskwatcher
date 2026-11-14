using ZendeskWatcher.Tracking;
using ZendeskWatcher.UI;

namespace ZendeskWatcher.SettingsPages;

/// <summary>
/// SLA (under Notifications): whether notifications show the deadline, and the targets by SLA type and priority for
/// tickets without a Zendesk SLA timer. Every SLA type seen in Zendesk gets a row, also new ones. Each target is the
/// team's default, estimated from Zendesk's SLA timers (types without defaults), or your own (with a warning: your
/// deadlines then differ from your colleagues'), color coded. Only your own are stored.
/// </summary>
internal sealed class SlaPage : SettingsPage
{
    private static readonly string[] Priorities = Sla.Priorities;

    /// <summary>The choices in each cell; short, so five columns fit. d = day, wd = working day.</summary>
    private static readonly Option<SlaTarget>[] Choices =
    [
        new(SlaTarget.Min(15), "15 min"), new(SlaTarget.Min(30), "30 min"), new(SlaTarget.Hours(1), "1 h"), new(SlaTarget.Hours(2), "2 h"),
        new(SlaTarget.Hours(4), "4 h"), new(SlaTarget.Hours(8), "8 h"), new(SlaTarget.Hours(16), "16 h"), new(SlaTarget.Hours(24), "1 d"),
        new(SlaTarget.Hours(48), "2 d"), new(SlaTarget.Days(1), "1 wd"), new(SlaTarget.Days(2), "2 wd"), new(SlaTarget.Days(3), "3 wd"),
        new(SlaTarget.Days(4), "4 wd")
    ];

    private readonly ToggleSwitch _show = new("Show the SLA deadline on ticket notifications");
    private readonly ChoicePills _which = new("First reply", "Follow-up");
    private readonly TableLayoutPanel _grid = new() { AutoSize = true, BackColor = Theme.Surface, Margin = new Padding(0, 10, 0, 4) };
    private readonly Label _explain = Theme.Label("", Theme.Small, Theme.TextMuted, Kit.CardWidth - 44);
    private readonly Label _warning = Theme.Label("You've changed targets (orange): your deadlines differ from your colleagues'. " +
        "Zendesk's own SLA timers still come first.", Theme.Small, Theme.Warning, Kit.CardWidth - 44);
    private readonly Label _reset = Kit.LinkLabel("Remove all my changes", Theme.Small);
    private readonly ToolTip _tips = new();

    /// <summary>The SLA types shown: the known ones, then any others seen in Zendesk (or in your changes).</summary>
    private readonly List<string> _types;
    private readonly SlaLearning _learned;

    /// <summary>The cells: light labels ("1 h ▾") that open a menu, not 20+ drop-downs (measured: those cost ~2 s and idle CPU).</summary>
    private readonly Label[,] _cells;

    /// <summary>Your own targets as edited (both tables), by type, priority column and first reply / follow-up.</summary>
    private readonly Dictionary<(string Type, int Column, bool FirstReply), SlaTarget> _custom = [];

    public SlaPage(SettingsContext context)
        : base(context, "SLA", "How quickly we answer, by the client's SLA type in Zendesk and the ticket's priority.")
    {
        // What the checks learned; before the first check of this run, as saved last time.
        _learned = SlaLearning.Current ?? WatchState.Load().Sla;
        _types = [.. Sla.KnownTypes, .. _learned.Types.Concat(context.Current().SlaOverrides.Select(o => o.Type))
            .Where(t => !Sla.KnownTypes.Contains(t)).Distinct().OrderBy(t => Sla.Name(t, _learned), StringComparer.CurrentCultureIgnoreCase)];
        _cells = new Label[_types.Count, Priorities.Length];

        var show = new Card("On notifications", "A deadline in red bold, e.g. “First reply due in 25 min” or “Reply overdue by 1 h”, " +
            "counting down while it's on screen. When Zendesk has an SLA timer on the ticket, that's the deadline.", Kit.CardWidth);
        show.Body.Controls.Add(_show);
        Panel.Controls.Add(show);

        var targets = new Card("Targets", "For tickets without a Zendesk SLA timer. Clients without an SLA type count as Standard; " +
            "new types in Zendesk get a row here.", Kit.CardWidth);
        targets.Body.Controls.Add(_which);
        targets.Body.Controls.Add(_grid);
        targets.Body.Controls.Add(Legend());
        targets.Body.Controls.Add(_explain);
        _warning.Margin = new Padding(0, 8, 0, 2);
        targets.Body.Controls.Add(_warning);
        targets.Body.Controls.Add(_reset);
        Panel.Controls.Add(targets);

        BuildGrid();
        _which.SelectedIndexChanged += (_, _) => Fill();
        _reset.Click += (_, _) =>
        {
            if (!ThemedDialog.Confirm(Context.Window, "Remove all your changes?",
                    "Every target goes back to the team's default or the estimate (not saved until you click Save).", "Remove"))
                return;
            _custom.Clear();
            Fill();
            Changed();
        };
        TrackChanges(_show);
        // Like Groups and Clients: changed only while signed in (the SLA types and estimates come from Zendesk).
        UseSignedOutStrip(readOnly =>
        {
            _show.Enabled = !readOnly;
            foreach (var cell in _cells)
                cell.Cursor = readOnly ? Cursors.Default : Cursors.Hand;
            Fill(); // greyed while locked
        }, "Not signed in: your choices still apply, but they're locked until you sign in.");
    }

    private Color ColorOf(SlaSource source) => Context.SignedOut ? Theme.TextMuted : source switch
    {
        SlaSource.Custom => Theme.Warning,
        SlaSource.Estimated => Theme.Accent,
        _ => Theme.Text
    };

    /// <summary>"● Default  ● Estimated from Zendesk  ● Yours", in the cells' colors.</summary>
    private Control Legend()
    {
        var legend = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Surface, Margin = new Padding(0, 2, 0, 4), WrapContents = false };
        foreach (var (source, text) in new[] { (SlaSource.Default, "Default"), (SlaSource.Estimated, "Estimated from Zendesk"), (SlaSource.Custom, "Yours") })
        {
            var label = Theme.Label($"● {text}", Theme.Small, source switch { SlaSource.Custom => Theme.Warning, SlaSource.Estimated => Theme.Accent, _ => Theme.Text });
            label.Margin = new Padding(0, 0, 14, 0);
            legend.Controls.Add(label);
        }
        return legend;
    }

    /// <summary>The grid, built once with the page (so it's scaled with it): SLA types down, priorities across.</summary>
    private void BuildGrid()
    {
        _grid.ColumnCount = Priorities.Length + 1;
        for (var c = 0; c <= Priorities.Length; c++)
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        static Label Cell(string text, Font font, Color color, bool left = false)
        {
            var label = Theme.Label(text, font, color);
            label.Margin = new Padding(left ? 0 : 4, 4, 4, 2);
            label.Anchor = AnchorStyles.Left;
            return label;
        }

        _grid.Controls.Add(Cell("", Theme.Small, Theme.TextMuted, left: true), 0, 0);
        string[] headers = ["P1 Urgent", "P2 High", "P3 Normal", "P4 Low"];
        for (var c = 0; c < headers.Length; c++)
            _grid.Controls.Add(Cell(headers[c], Theme.Small, Theme.TextMuted), c + 1, 0);
        for (var r = 0; r < _types.Count; r++)
        {
            _grid.Controls.Add(Cell(Sla.Name(_types[r], _learned), Theme.Body, Theme.Text, left: true), 0, r + 1);
            for (var c = 0; c < Priorities.Length; c++)
            {
                var cell = Cell("", Theme.BodyBold, Theme.Text);
                cell.Margin = new Padding(14, 4, 4, 4);
                cell.Cursor = Cursors.Hand;
                var (type, column) = (_types[r], c);
                cell.Click += (_, _) => Choose(cell, type, column);
                _grid.Controls.Add(_cells[r, c] = cell, c + 1, r + 1);
            }
        }
    }

    /// <summary>The target a cell shows, and where it comes from (your change first).</summary>
    private (SlaTarget Target, SlaSource Source) Current(string type, int column, bool firstReply) =>
        _custom.TryGetValue((type, column, firstReply), out var mine) ? (mine, SlaSource.Custom)
        : Sla.Resolve(type, Priorities[column], firstReply, null, _learned);

    private static string LabelOf(SlaTarget target) => Choices.FirstOrDefault(o => o.Value == target)?.Label ?? target.Label;

    /// <summary>The choices for one cell, under it; the current one ticked, the default and the estimate marked.</summary>
    private void Choose(Label cell, string type, int column)
    {
        if (Context.SignedOut)
            return;
        var firstReply = _which.SelectedIndex == 0;
        var (current, source) = Current(type, column, firstReply);
        var team = Sla.TeamTarget(type, Priorities[column], firstReply);
        var estimate = _learned.Estimate(type, Priorities[column], firstReply)?.Target;
        var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        if (source == SlaSource.Custom)
        {
            var undo = new ToolStripMenuItem("Remove my change");
            undo.Click += (_, _) => { _custom.Remove((type, column, firstReply)); Fill(); Changed(); };
            menu.Items.Add(undo);
            menu.Items.Add(new ToolStripSeparator());
        }
        foreach (var choice in Choices.Concat(estimate is { } e && Choices.All(o => o.Value != e) ? [new Option<SlaTarget>(e, e.Label)] : []))
        {
            var marks = (choice.Value == team ? "  (default)" : "") + (choice.Value == estimate ? "  (estimated)" : "");
            var item = new ToolStripMenuItem(choice.Label + marks) { Checked = choice.Value == current };
            item.Click += (_, _) =>
            {
                // Picking what it would be anyway isn't a change of yours.
                if (Sla.Resolve(type, Priorities[column], firstReply, null, _learned).Target == choice.Value)
                    _custom.Remove((type, column, firstReply));
                else
                    _custom[(type, column, firstReply)] = choice.Value;
                Fill();
                Changed();
            };
            menu.Items.Add(item);
        }
        ThemedMenu.Apply(menu, Theme.Current);
        menu.Closed += (_, _) => menu.BeginInvoke(menu.Dispose); // after the click is handled
        menu.Show(cell, new Point(0, cell.Height));
    }

    /// <summary>Shows First reply or Follow-up in the cells, color coded by where each target comes from.</summary>
    private void Fill()
    {
        var firstReply = _which.SelectedIndex == 0;
        for (var r = 0; r < _types.Count; r++)
            for (var c = 0; c < Priorities.Length; c++)
            {
                var (type, priority) = (_types[r], Priorities[c]);
                var (target, source) = Current(type, c, firstReply);
                var team = Sla.TeamTarget(type, priority, firstReply);
                var estimate = _learned.Estimate(type, priority, firstReply);
                _cells[r, c].Text = $"{LabelOf(target)} ▾";
                _cells[r, c].ForeColor = ColorOf(source);
                _tips.SetToolTip(_cells[r, c], source switch
                {
                    SlaSource.Custom => $"Yours. {(team is { } t ? $"Default: {t.Label}." : estimate is { } est ? $"Estimated: {est.Target.Label}." : "")}",
                    SlaSource.Estimated => $"Estimated from {estimate!.Value.Tickets} tickets' SLA timers in Zendesk.",
                    _ when team is null => "No default or estimate for this type yet: Standard's target is used until Zendesk's timers show its own.",
                    _ => estimate is { } est2 && est2.Target != target
                        ? $"Default. Zendesk's timers suggest {est2.Target.Label} ({est2.Tickets} tickets)."
                        : "Default (the team's target)."
                });
            }
        _explain.Text = (firstReply
            ? "First reply: a new unassigned ticket, counted from when it was created."
            : "Follow-up: a customer waiting for an answer on an assigned ticket, counted from when they wrote.") +
            " P1, 24x7, Urgent (15 Min) and estimates count around the clock; the others only Monday to Friday. " +
            "d = day, wd = working day. Hover a target for details.";
        _warning.Visible = _custom.Count > 0;
        _reset.Visible = _custom.Count > 0 && !Context.SignedOut;
    }

    public override bool IsSubPage => true;

    public override void Load(Settings settings)
    {
        _show.Checked = settings.ShowSlaDeadlines;
        _custom.Clear();
        foreach (var o in settings.SlaOverrides)
            _custom[(o.Type, Sla.Column(o.Priority), o.FirstReply)] = new SlaTarget(o.Minutes, o.WorkingDays);
        Fill();
    }

    public override void Apply(Settings settings)
    {
        settings.ShowSlaDeadlines = _show.Checked;
        settings.SlaOverrides = _custom
            .OrderBy(kv => kv.Key.Type).ThenBy(kv => kv.Key.Column).ThenBy(kv => !kv.Key.FirstReply)
            .Select(kv => new SlaOverride { Type = kv.Key.Type, Priority = Priorities[kv.Key.Column], FirstReply = kv.Key.FirstReply,
                Minutes = kv.Value.Minutes, WorkingDays = kv.Value.WorkingDays })
            .ToList();
    }
}
