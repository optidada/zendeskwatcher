namespace ZendeskWatcher.Tests;

/// <summary>settings.json on disk: older versions' files load completely, and a damaged file is never silently lost.</summary>
public class SettingsFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ZendeskWatcher.SettingsFileTests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    public SettingsFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>As 1.3.10 saved it: no CC style, app theme or display times yet; ticket styles still had a Display.</summary>
    private const string From1310 = """
        {
          "Subdomain": "acme",
          "WatchedGroups": [ { "Id": 1, "Name": "Support Tier 2" }, { "Id": 2, "Name": "Premium Support" } ],
          "PollIntervalSeconds": 120,
          "TrackMyTickets": true,
          "TrackCcTickets": true,
          "NotifyUnassigned": true,
          "NotifyPublicReplies": true,
          "NotifyInternalNotes": false,
          "NotifyStatusChanges": true,
          "NotifyAssignmentChanges": true,
          "NotifyPriorityChanges": false,
          "HelpColleagues": true,
          "HelpSameAsMonitored": false,
          "HelpGroups": [ { "Id": 4, "Name": "Onboarding" } ],
          "HelpExcludedAssignees": [ { "Id": 9, "Name": "Alex Example" } ],
          "WorkdayStartHour": 9,
          "WorkdayEndHour": 18,
          "HelpPriorities": [ "urgent" ],
          "HelpOnlyDuringMyHours": true,
          "ShowStartSummary": false,
          "SnoozeMinutes": 30,
          "SnoozeBehavior": 1,
          "ShowCommentText": false,
          "NewTicketStyle": { "Sound": 6, "Display": 1 },
          "MyTicketStyle": { "Sound": 1, "Display": 0 },
          "HelpStyle": { "Sound": 3, "Display": 1 },
          "Theme": 2,
          "Position": 1,
          "ShowAppLogo": false,
          "GroupNotificationsByType": true
        }
        """;

    [Fact]
    public void A_file_from_an_older_version_loads_with_nothing_lost()
    {
        File.WriteAllText(FilePath, From1310);

        var settings = Settings.Load(FilePath);

        Assert.Null(Settings.LoadProblem);
        Assert.Equal("acme", settings.Subdomain);
        Assert.Equal(["Support Tier 2", "Premium Support"], settings.WatchedGroups.Select(g => g.Name));
        Assert.Equal(120, settings.PollIntervalSeconds);
        Assert.True(settings.TrackCcTickets);
        Assert.False(settings.NotifyInternalNotes);
        Assert.Equal("Onboarding", Assert.Single(settings.HelpGroups).Name);
        Assert.Equal(9, Assert.Single(settings.HelpExcludedAssignees).Id);
        Assert.Equal(["urgent"], settings.HelpPriorities);
        Assert.Equal(30, settings.SnoozeMinutes);
        Assert.Equal(ToastSound.ChipBlip, settings.NewTicketStyle.Sound);
        Assert.Equal(ToastSound.Mail, settings.CcTicketStyle.Sound); // CC shared "My tickets" back then
        Assert.Equal(NotificationTheme.CleanLight, settings.Theme);
        Assert.Equal(PopupPosition.TopRight, settings.Position);
        Assert.Equal(30, settings.TicketDisplay.Normal); // new settings get their defaults
    }

    [Fact]
    public void A_damaged_file_is_kept_and_the_copy_from_before_the_last_save_is_used()
    {
        new Settings { Subdomain = "acme", WatchedGroups = [new WatchedGroup { Id = 1, Name = "Tier 2" }] }.Save(FilePath);
        new Settings { Subdomain = "acme", WatchedGroups = [new WatchedGroup { Id = 1, Name = "Tier 2" }], SnoozeMinutes = 30 }.Save(FilePath);
        File.WriteAllText(FilePath, "{ \"Subdomain\": \"acme\", \"WatchedGr"); // e.g. cut off by a crash

        var settings = Settings.Load(FilePath);

        Assert.Equal("Tier 2", Assert.Single(settings.WatchedGroups).Name);
        Assert.Equal(10, settings.SnoozeMinutes); // the first save: the one before the damaged file
        Assert.Contains("copy from before your last change", Settings.LoadProblem);
        Assert.Equal("{ \"Subdomain\": \"acme\", \"WatchedGr", File.ReadAllText(FilePath + ".bad"));
        Assert.False(Settings.IsFirstRun);
    }

    [Fact]
    public void Without_a_backup_a_damaged_file_gives_the_defaults_and_says_so()
    {
        File.WriteAllText(FilePath, "");

        var settings = Settings.Load(FilePath);

        Assert.Empty(settings.WatchedGroups);
        Assert.Contains("defaults are used", Settings.LoadProblem);
        Assert.True(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void Saving_keeps_the_previous_file_and_leaves_no_temp_file()
    {
        new Settings { Subdomain = "first" }.Save(FilePath);
        new Settings { Subdomain = "second" }.Save(FilePath);

        Assert.Equal("second", Settings.Load(FilePath).Subdomain);
        Assert.Equal("first", Settings.FromJson(File.ReadAllText(FilePath + ".bak")).Subdomain);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void No_file_is_the_first_start()
    {
        Settings.Load(FilePath);

        Assert.True(Settings.IsFirstRun);
        Assert.Null(Settings.LoadProblem);
    }
}
