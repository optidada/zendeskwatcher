using Xunit.Abstractions;
using ZendeskWatcher.Tracking;

namespace ZendeskWatcher.Tests;

/// <summary>The bundled language model loads and puts texts about the same thing close together.</summary>
public class EmbeddingsTests(ITestOutputHelper output)
{
    private static readonly Embeddings Model = Embeddings.Shared ?? throw new InvalidOperationException("model not loaded");

    private float Sim(string a, string b)
    {
        var s = Embeddings.Similarity(Model.Embed(a), Model.Embed(b));
        output.WriteLine($"{s:0.00}  {a}  |  {b}");
        return s;
    }

    [Fact]
    public void The_model_is_in_the_app_and_loads()
    {
        Assert.Equal(256, Model.Dimensions);
        var v = Model.Embed("Search results missing after deploy");
        Assert.Equal(1, Math.Sqrt(v.Sum(x => x * x)), 3);
    }

    [Fact]
    public void Word_pieces_follow_the_model_tokenizer()
    {
        Assert.Equal(["can", "'", "t", "log", "in", "!"], Embeddings.Words("Can't log in!").ToArray());
        Assert.NotEmpty(Model.TokenIds("unpublished"));
        _ = Model.TokenIds("qqqqzzzzxxxx### 2603").ToList(); // unknown pieces never throw
    }

    [Fact]
    public void Same_issue_in_other_words_is_closer_than_a_different_issue()
    {
        Assert.True(Sim("Website is down", "Site not loading for anyone") > Sim("Website is down", "Invoice for October"));
        Assert.True(Sim("Cannot log in to the CMS", "Login fails for editors") > Sim("Cannot log in to the CMS", "Search index out of date"));
        Assert.True(Sim("Checkout broken", "Customers can't pay for orders") > Sim("Checkout broken", "Change the logo in the header"));
        Assert.True(Sim("Search results missing after deploy", "Search not returning new pages") > Sim("Search results missing after deploy", "SSL certificate expires soon"));
    }

    [Fact]
    public void Prints_typical_scores_for_tuning()
    {
        foreach (var (a, b) in new[]
        {
            ("Site down", "Website down"), ("Site down", "Our website is unavailable"), ("Site down", "Production outage"),
            ("Site down", "500 error on homepage"), ("Images not showing in media library", "Media library images broken"),
            ("Scheduled publishing stuck in queue", "Scheduled jobs not running"), ("Add new user to account", "Request access for colleague"),
            ("Site down", "How do I add a block?"), ("Login fails", "Invoice question"), ("Search not working", "Find feature broken"),
            ("DXP deployment failed", "Deployment to production fails"), ("Commerce order export", "Change the logo color"),
        })
            Sim(a, b);
    }
}
