namespace ZendeskWatcher.Zendesk;

/// <summary>The raw result of an API GET.</summary>
public sealed record ApiResponse(int Status, string Body, TimeSpan? RetryAfter)
{
    public bool IsSuccess => Status is >= 200 and < 300;
}

/// <summary>How API requests are authenticated (currently: the agent's browser login session).</summary>
public interface IZendeskSession
{
    /// <summary>True when a request may work without asking the user to sign in first.</summary>
    bool MightBeSignedIn { get; }

    /// <summary>GETs an absolute API URL. A missing or expired login is reported as status 401.</summary>
    Task<ApiResponse> GetAsync(string url, CancellationToken ct);

    /// <summary>Tries to get a working login again without user interaction.</summary>
    Task<bool> TryRecoverAsync(CancellationToken ct);

    /// <summary>Interactive sign-in. Throws <see cref="OperationCanceledException"/> when the user gives up.</summary>
    Task SignInAsync(IWin32Window? owner, CancellationToken ct);

    void SignOut();
}
