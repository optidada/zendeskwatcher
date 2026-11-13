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
    public void Script_is_one_function_with_the_field_lists()
    {
        Assert.StartsWith("(text => {", ResponseSlimming.Script);
        Assert.Contains("\"assignee_id\"", ResponseSlimming.Script);
        Assert.Contains("return text;", ResponseSlimming.Script); // anything unexpected passes through unchanged
    }
}
