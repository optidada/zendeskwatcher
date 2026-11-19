namespace ZendeskWatcher.UI;

/// <summary>
/// The team's definitions of the priority levels, on How it works: a reminder when a client's chosen priority doesn't
/// match the case. Say "client", never "customer", and never "Sev 1" etc.
/// </summary>
public static class PriorityLevels
{
    /// <summary>"P1" … "P4" with what each means, most urgent first.</summary>
    public static readonly (string Tag, string Definition)[] All =
    [
        ("P1", "Critical production issue that stops the client using the service or halts their business operations, with no procedural workaround."),
        ("P2", "Production issue where major functionality is severely impaired or performance is significantly degraded, with no reasonable workaround."),
        ("P3", "Partial, non-critical loss of functionality. A short-term workaround exists but isn't scalable."),
        ("P4", "General technical enquiries, cosmetic issues, and documentation errors.")
    ];
}
