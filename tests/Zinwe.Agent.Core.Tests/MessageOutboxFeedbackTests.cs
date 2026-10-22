using Zinwe.Agent.Core.Messages;

namespace Zinwe.Agent.Core.Tests;

/// <summary>FR-14: feedback rides the member's outbox but never shows up as a chat line.</summary>
public sealed class MessageOutboxFeedbackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zinwe-outbox-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Feedback_is_kept_apart_from_chat_replies()
    {
        var outbox = new MessageOutbox(_dir);
        outbox.Append("On my way");
        var id = outbox.AppendFeedback(8, "Easy to use");

        Assert.Equal(["On my way"], outbox.Pending().Select(r => r.Body));
        var feedback = Assert.Single(outbox.PendingFeedback());
        Assert.Equal((id, 8, "Easy to use"), (feedback.Id, feedback.Rating, feedback.Body));
        Assert.Equal(2, outbox.All().Count);

        outbox.Remove([id]);
        Assert.Empty(outbox.PendingFeedback());
        Assert.Single(outbox.Pending());
    }

    [Fact]
    public void Rating_is_clamped_to_one_to_ten()
    {
        var outbox = new MessageOutbox(_dir);
        outbox.AppendFeedback(42, "");
        outbox.AppendFeedback(-1, "");
        Assert.Equal([10, 1], outbox.PendingFeedback().Select(f => f.Rating ?? 0));
    }

    [Fact]
    public void An_old_outbox_file_reads_as_replies()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "messages-outbox.json"),
            """{"Items":[{"Id":"a1","Body":"hi","CreatedAtUtc":"2026-10-22T10:00:00+00:00"}]}""");
        var outbox = new MessageOutbox(_dir);
        Assert.Equal("hi", Assert.Single(outbox.Pending()).Body);
        Assert.Empty(outbox.PendingFeedback());
    }
}
