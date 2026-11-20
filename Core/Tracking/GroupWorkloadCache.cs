using System.Text.Json;
using System.Text.Json.Serialization;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tracking;

/// <summary>
/// A group's members and assigned open tickets, shared by Insights → Team workload, "Who could take it" (▾ next to
/// Take it) and Cover today: all need exactly this, and a big group takes a while to read (all its open tickets). Also
/// each suggested person's unsolved tickets in all groups (<see cref="GetPersonAsync"/>), same rules.
/// <para>
/// Kept on disk (cache\group-workload.json: ticket number, assignee, status, priority; never subject or text), so the
/// first click after a restart is as fast as a later one. Each caller says how fresh it wants it; older than that but
/// within <see cref="CacheAges.WorkloadKeep"/> it's answered at once and read again in the background, once (a read
/// under way is shared, never started twice). Members change rarely: read again only after
/// <see cref="CacheAges.MembersFresh"/>, so a group read is usually one request. After an assign the workload must be read
/// again (<see cref="Clear"/>); members stay.
/// </para>
/// </summary>
public static class GroupWorkloadCache
{
    public sealed record Entry(DateTime At, List<User> Members, List<Ticket> Tickets);

    /// <summary>As kept in memory and on disk. Times in UTC; MinValue: must be read before use.</summary>
    internal sealed class Group
    {
        public string Subdomain { get; set; } = "";
        public long Id { get; set; }
        public DateTime MembersAt { get; set; }
        public List<User> Members { get; set; } = [];
        public DateTime TicketsAt { get; set; }
        public List<Ticket> Tickets { get; set; } = [];
    }

    internal sealed class Person
    {
        public string Subdomain { get; set; } = "";
        public long Id { get; set; }
        public DateTime At { get; set; }
        public List<Ticket> Tickets { get; set; } = [];
    }

    private sealed class Saved
    {
        public List<Group> Groups { get; set; } = [];
        public List<Person> People { get; set; } = [];
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<(string Subdomain, long Group), Group> Groups = [];
    private static readonly Dictionary<(string Subdomain, long User), Person> People = [];
    private static readonly Dictionary<(string Subdomain, long Group), Task<Group>> GroupReads = [];
    private static readonly Dictionary<(string Subdomain, long User), Task<Person>> PersonReads = [];
    private static bool _loaded;

    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };
    private static string FileFor() => Path.Combine(AppPaths.DataDir, "cache", "group-workload.json");

    /// <summary>
    /// The group's members (agents, not end users) and assigned open tickets: read at most <paramref name="fresh"/> ago,
    /// or up to <see cref="CacheAges.WorkloadKeep"/> while a fresh read runs in the background. With
    /// <paramref name="waitWhenStale"/> (Insights, which shows its own last result meanwhile) older than fresh is waited for.
    /// </summary>
    public static async Task<Entry> GetAsync(ZendeskClient client, string subdomain, long groupId, TimeSpan fresh, CancellationToken ct,
        bool waitWhenStale = false)
    {
        Task<Group> read;
        lock (Gate)
        {
            LoadOnce();
            var key = (subdomain, groupId);
            var now = DateTime.UtcNow;
            var keep = waitWhenStale ? fresh : CacheAges.WorkloadKeep;
            if (Groups.TryGetValue(key, out var known) && now - known.MembersAt <= CacheAges.MembersKeep && now - known.TicketsAt <= keep)
            {
                if (now - known.TicketsAt > fresh || now - known.MembersAt > CacheAges.MembersFresh)
                    StartGroupRead(client, key, known); // for next time; this answer doesn't wait
                return ToEntry(known);
            }
            read = StartGroupRead(client, key, known);
        }
        return ToEntry(await read.WaitAsync(ct));
    }

    /// <summary>A person's unsolved tickets in every group, read at most <paramref name="fresh"/> ago (or kept, as <see cref="GetAsync"/>).</summary>
    public static async Task<List<Ticket>> GetPersonAsync(ZendeskClient client, string subdomain, long userId, TimeSpan fresh, CancellationToken ct)
    {
        Task<Person> read;
        lock (Gate)
        {
            LoadOnce();
            var key = (subdomain, userId);
            if (People.TryGetValue(key, out var known) && DateTime.UtcNow - known.At <= CacheAges.WorkloadKeep)
            {
                if (DateTime.UtcNow - known.At > fresh)
                    StartPersonRead(client, key);
                return known.Tickets;
            }
            read = StartPersonRead(client, key);
        }
        return (await read.WaitAsync(ct)).Tickets;
    }

    /// <summary>After an assign (someone's count changed): every workload is read again before it's used. Members stay.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            foreach (var group in Groups.Values)
                group.TicketsAt = DateTime.MinValue;
            foreach (var person in People.Values)
                person.At = DateTime.MinValue;
        }
    }

    /// <summary>Developer switch --measure-take and tests: forgets everything in memory and reads the file again, as after a restart.</summary>
    internal static void Reload()
    {
        lock (Gate)
        {
            Groups.Clear();
            People.Clear();
            _loaded = false;
        }
    }

    /// <summary>Tests: forgets everything, members too, in memory and on disk.</summary>
    internal static void Forget()
    {
        lock (Gate)
        {
            Groups.Clear();
            People.Clear();
            _loaded = true;
        }
        try { lock (FileGate) File.Delete(FileFor()); } catch (IOException) { }
    }

    /// <summary>Tests: waits for the reads under way (background ones too).</summary>
    internal static async Task WhenIdleAsync()
    {
        Task[] running;
        lock (Gate)
            running = [.. GroupReads.Values, .. PersonReads.Values];
        try { await Task.WhenAll(running); }
        catch { /* a failed read is the caller's business */ }
    }

    /// <summary>Under <see cref="Gate"/>. Joins a read under way. Not tied to one caller's cancellation: shared, it runs to the end.</summary>
    private static Task<Group> StartGroupRead(ZendeskClient client, (string Subdomain, long Group) key, Group? known)
    {
        if (GroupReads.TryGetValue(key, out var running))
            return running;
        var withMembers = known is null || DateTime.UtcNow - known.MembersAt > CacheAges.MembersFresh;
        var read = ReadGroupAsync(client, key, withMembers ? null : known!.Members);
        GroupReads[key] = read;
        _ = read.ContinueWith(t => Done(t, () => GroupReads.Remove(key), $"group {key.Group}"), TaskScheduler.Default);
        return read;
    }

    private static Task<Person> StartPersonRead(ZendeskClient client, (string Subdomain, long User) key)
    {
        if (PersonReads.TryGetValue(key, out var running))
            return running;
        var read = ReadPersonAsync(client, key);
        PersonReads[key] = read;
        _ = read.ContinueWith(t => Done(t, () => PersonReads.Remove(key), $"user {key.User}"), TaskScheduler.Default);
        return read;
    }

    /// <summary>A read ended: no longer under way. A failure is logged here too (a background read has no one waiting).</summary>
    private static void Done(Task read, Action forget, string what)
    {
        lock (Gate)
            forget();
        if (read.Exception?.InnerException is { } ex and not AuthRequiredException)
            Log.Info($"Could not read the workload of {what} ({ex.GetType().Name}: {ex.Message})");
    }

    private static async Task<Group> ReadGroupAsync(ZendeskClient client, (string Subdomain, long Group) key, List<User>? knownMembers)
    {
        await Task.Yield(); // not inside the caller's lock
        var members = knownMembers is null ? client.GetGroupMembersAsync(key.Group, CancellationToken.None) : Task.FromResult(knownMembers);
        var tickets = client.GetAssignedOpenTicketsAsync(key.Group, CancellationToken.None);
        await Task.WhenAll(members, tickets);
        var now = DateTime.UtcNow;
        Group group;
        lock (Gate)
        {
            var old = Groups.GetValueOrDefault(key);
            group = new Group
            {
                Subdomain = key.Subdomain, Id = key.Group, TicketsAt = now, Tickets = Slim(tickets.Result),
                MembersAt = knownMembers is null ? now : old?.MembersAt ?? now,
                Members = knownMembers ?? [.. members.Result.Where(u => u.Role != "end-user").DistinctBy(u => u.Id).Select(SlimUser)]
            };
            Groups[key] = group;
        }
        Save();
        return group;
    }

    private static async Task<Person> ReadPersonAsync(ZendeskClient client, (string Subdomain, long User) key)
    {
        await Task.Yield();
        var person = new Person { Subdomain = key.Subdomain, Id = key.User, At = DateTime.UtcNow, Tickets = Slim(await client.GetOpenTicketsAssignedToAsync(key.User, CancellationToken.None)) };
        lock (Gate)
            People[key] = person;
        Save();
        return person;
    }

    private static Entry ToEntry(Group group) => new(group.TicketsAt, group.Members, group.Tickets);

    /// <summary>Under <see cref="Gate"/>: what was saved before the app started, the parts still worth keeping.</summary>
    private static void LoadOnce()
    {
        if (_loaded)
            return;
        _loaded = true;
        try
        {
            var file = FileFor();
            if (!File.Exists(file) || JsonSerializer.Deserialize<Saved>(File.ReadAllText(file), JsonOptions) is not { } saved)
                return;
            var now = DateTime.UtcNow;
            var oldStyle = false; // saved by an older version with more (full addresses, every tag): rewritten trimmed now
            foreach (var group in saved.Groups.Where(g => now - g.MembersAt <= CacheAges.MembersKeep))
            {
                oldStyle |= group.Members.Any(m => m.Email is { } email && !email.StartsWith('*') || m.Tags.Count > SlimUser(m).Tags.Count);
                group.Members = [.. group.Members.Select(SlimUser)];
                if (now - group.TicketsAt > CacheAges.WorkloadKeep)
                    group.TicketsAt = DateTime.MinValue; // members still good, workload must be read
                Groups.TryAdd((group.Subdomain, group.Id), group);
            }
            foreach (var person in saved.People.Where(p => now - p.At <= CacheAges.WorkloadKeep))
                People.TryAdd((person.Subdomain, person.Id), person);
            if (oldStyle)
                Save(); // under Gate: it takes it again (same thread), then writes
        }
        catch (Exception ex)
        {
            Log.Error("Could not read the saved group workload", ex);
        }
    }

    private static readonly object FileGate = new();

    private static void Save()
    {
        string json;
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            json = JsonSerializer.Serialize(new Saved
            {
                Groups = [.. Groups.Values.Where(g => now - g.MembersAt <= CacheAges.MembersKeep)],
                People = [.. People.Values.Where(p => now - p.At <= CacheAges.WorkloadKeep)]
            }, JsonOptions);
        }
        try
        {
            lock (FileGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FileFor())!);
                File.WriteAllText(FileFor(), json);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not save the group workload", ex);
        }
    }

    /// <summary>
    /// Only what the counts read (<see cref="TeamWorkload.Count"/>): the subject, the customer's message, tags and the rest
    /// aren't kept (a few KB per group instead of hundreds), and nothing written in a ticket goes to disk.
    /// </summary>
    internal static List<Ticket> Slim(List<Ticket> tickets) =>
        [.. tickets.Select(t => new Ticket { Id = t.Id, AssigneeId = t.AssigneeId, Status = t.Status, CustomStatusId = t.CustomStatusId, Priority = t.Priority })];

    /// <summary>A member as the rules need them, as users are kept on disk (<see cref="UserStore.ForDisk"/>): name, role, time zone.</summary>
    private static User SlimUser(User u) => UserStore.ForDisk(u);
}
