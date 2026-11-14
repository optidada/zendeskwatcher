using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tests;

/// <summary>The browser keeps only the fields the models read: the lists must follow the models.</summary>
public class ResponseSlimmingTests
{
    [Fact]
    public void Ticket_fields_are_the_ones_the_model_reads()
    {
        var fields = ResponseSlimming.FieldsOf<Ticket>();

        Assert.Contains("id", fields);
        Assert.Contains("assignee_id", fields);
        Assert.Contains("custom_status_id", fields);
        Assert.Contains("email_cc_ids", fields);
        Assert.Contains("updated_at", fields);
        Assert.DoesNotContain("is_open", fields); // computed, not from Zendesk
        Assert.DoesNotContain("description", fields); // the long first message never crosses over
    }

    [Fact]
    public void Audit_events_keep_the_comment_text_used_in_notifications()
    {
        Assert.Contains("events", ResponseSlimming.FieldsOf<Audit>());
        var events = ResponseSlimming.FieldsOf<AuditEvent>();
        Assert.Contains("plain_body", events);
        Assert.Contains("previous_value", events);
        Assert.DoesNotContain("html_body", events);
    }

    [Fact]
    public void An_event_whose_body_is_not_text_does_not_break_reading_the_audits()
    {
        // Seen in Catch up (1.5.0): a chat/call event sends "body" as an object.
        const string json = """
            {"audits":[{"id":1,"author_id":5,"events":[{"type":"ChatStartedEvent","body":{"history":[1,2]},"plain_body":["x"]}]},
                       {"id":2,"author_id":5,"events":[{"type":"Comment","public":true,"body":"Hello","plain_body":"Hello"}]}]}
            """;
        var options = new System.Text.Json.JsonSerializerOptions
            { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };

        var audits = System.Text.Json.JsonSerializer.Deserialize<AuditsResponse>(json, options)!.Audits;

        Assert.Null(audits[0].Events[0].Body);
        Assert.Null(audits[0].Events[0].PlainBody);
        Assert.Equal("Hello", audits[1].Events[0].PlainBody);
    }

    [Fact]
    public void Script_is_one_function_with_the_field_lists()
    {
        Assert.StartsWith("(text => {", ResponseSlimming.Script);
        Assert.Contains("\"assignee_id\"", ResponseSlimming.Script);
        Assert.Contains("return text;", ResponseSlimming.Script); // anything unexpected passes through unchanged
    }
}
