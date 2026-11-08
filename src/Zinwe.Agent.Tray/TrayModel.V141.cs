using Zinwe.Agent.Core.Chat;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Core.Work;

namespace Zinwe.Agent.Tray;

/// <summary>One of the member's asks for a day off as Home lists it ("Thu, 12 Nov · Full day", "Waiting").</summary>
public sealed record LeaveRow(string Label, string Status, string Note, bool Sending);

public sealed partial class TrayModel
{
    // ======================================================================================================
    // v1.4.1 · edit / delete your own lines, @mentions, deleted groups, refusals, asking for a day off
    // ======================================================================================================

    private readonly HashSet<long> _goneSeen = [];
    private readonly HashSet<string> _noticesShown = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _leaveSeen = [];

    /// <summary>The running service knows edits, deletes and deleted groups (1.4.1): a 1.4.0 one would drop them.</summary>
    public bool ChatEditsOn => Status?.ChatVersion is not null && _chat?.Gone is not null;

    // ---- Edit and delete ------------------------------------------------------------------------------

    /// <summary>The own line the composer is editing (Enter saves, Esc cancels).</summary>
    public TeamMessage? TeamEditing { get; private set; }

    /// <summary>The own line waiting for "Delete for everyone?".</summary>
    public TeamMessage? DeleteAsk { get; private set; }

    public void StartEdit(TeamMessage m)
    {
        if (!ChatEditsOn)
        {
            return;
        }
        if (!ChatEdits.CanChange(m, mine: true, Now))
        {
            ShowSnack(ChatEdits.TooLate, [("OK", CloseSnack)]);
            return;
        }
        TeamEditing = m;
        TeamReplyTo = null;
        DeleteAsk = null;
        Raise();
        FocusRequested?.Invoke("team-composer");
    }

    public void CancelEdit()
    {
        TeamEditing = null;
        Raise();
    }

    /// <summary>Save the edit (it goes through the outbox like a line). True when the composer may clear.</summary>
    public bool SaveEdit(string text)
    {
        if (TeamEditing is not { } m || ChatOpen is not { } key)
        {
            return false;
        }
        var body = (text ?? "").Trim();
        if (body.Length == 0)
        {
            ShowSnack("Type something, or delete the message instead.", [("OK", CloseSnack)]);
            return false;
        }
        TeamEditing = null;
        if (body == m.Body.Trim())
        {
            Raise();
            return true;
        }
        if (!ChatEdits.CanChange(m, mine: true, Now))
        {
            ShowSnack(ChatEdits.TooLate, [("OK", CloseSnack)]);
            return true;
        }
        QueueChat(OutboxReply.ChatEdit, key, body, m.Id);
        Raise();
        _ = RefreshChatAsync();
        return true;
    }

    public void AskDelete(TeamMessage m)
    {
        if (!ChatEditsOn)
        {
            return;
        }
        if (!ChatEdits.CanChange(m, mine: true, Now))
        {
            ShowSnack(ChatEdits.TooLate, [("OK", CloseSnack)]);
            return;
        }
        DeleteAsk = m;
        Raise();
    }

    public void CancelDelete()
    {
        DeleteAsk = null;
        Raise();
    }

    /// <summary>"Delete": everyone then sees "This message was deleted".</summary>
    public void ConfirmDelete()
    {
        if (DeleteAsk is { } m && ChatOpen is { } key)
        {
            if (ChatEdits.CanChange(m, mine: true, Now))
            {
                QueueChat(OutboxReply.ChatDelete, key, "", m.Id);
                if (TeamEditing?.Id == m.Id)
                {
                    TeamEditing = null;
                }
            }
            else
            {
                ShowSnack(ChatEdits.TooLate, [("OK", CloseSnack)]);
            }
        }
        DeleteAsk = null;
        Raise();
        _ = RefreshChatAsync();
    }

    /// <summary>The edits and deletes of a thread still in the outbox (the newest per line; a delete wins).</summary>
    private Dictionary<long, (string Action, string Body)> PendingChanges(string key)
    {
        var changes = new Dictionary<long, (string, string)>();
        try
        {
            foreach (var i in _outbox.PendingChat().Where(i => i.ChatKey == key && i.MessageId is not null
                && i.Action is OutboxReply.ChatEdit or OutboxReply.ChatDelete))
            {
                if (!(changes.TryGetValue(i.MessageId!.Value, out var had) && had.Item1 == OutboxReply.ChatDelete))
                {
                    changes[i.MessageId.Value] = (i.Action!, i.Body);
                }
            }
        }
        catch (Exception)
        {
            // the file is busy: the next refresh shows them
        }
        return changes;
    }

    // ---- @mentions -------------------------------------------------------------------------------------

    /// <summary>The member's name as their groups have it (else <see cref="MyName"/>): what "@you" matches.</summary>
    public string MyMentionName =>
        (MeId is { } me ? Conversations.SelectMany(c => c.Members ?? []).FirstOrDefault(p => p.MemberId == me && p.Name.Length > 0)?.Name : null)
        ?? MyName;

    /// <summary>The people the open group's composer offers after "@": its members (not you) and the admin.</summary>
    public IReadOnlyList<TeamMember> MentionPeople
    {
        get
        {
            if (OpenConversation is not { IsGroup: true } c)
            {
                return [];
            }
            var me = MeId;
            var mine = MyMentionName;
            var people = (c.Members ?? []).Where(p => p.MemberId != me && p.Name.Length > 0
                && !string.Equals(p.Name, mine, StringComparison.OrdinalIgnoreCase)).ToList();
            if (Status?.OwnerName is { Length: > 0 } owner && people.All(p => !string.Equals(p.Name, owner.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                people.Add(new TeamMember(0, owner.Trim(), "Admin"));
            }
            return people;
        }
    }

    /// <summary>Every name an "@" in the open thread may stand for (drawn in a stronger style).</summary>
    public IReadOnlyList<string> MentionNames => [.. MentionPeople.Select(p => p.Name), MyMentionName];

    /// <summary>A group's newest line names the member (and isn't theirs).</summary>
    private bool LastMentionsMe(TeamConversation c)
    {
        if (!c.IsGroup || c.Last is not { Deleted: false } l || l.TaskId is not null)
        {
            return false;
        }
        var mine = l.Mine ?? (MeId is { } me && l.ByMemberId == me && !l.ByOwner);
        return !mine && Mentions.MentionsMe(l.Body, MyMentionName);
    }

    // ---- Deleted groups and refusals ------------------------------------------------------------------

    /// <summary>
    /// A conversation the service says was deleted: its local choices go, and an open thread closes with a short note.
    /// </summary>
    private void HandleGone(ChatReply reply, ChatReply? before)
    {
        foreach (var id in reply.Gone ?? [])
        {
            if (!_goneSeen.Add(id))
            {
                continue;
            }
            var key = ChatKeys.Conversation(id);
            var forgotten = _prefs.Forget(key);
            if (!ReferenceEquals(forgotten, _prefs))
            {
                _prefs = forgotten;
                _prefs.Save();
            }
            _unreadSeen.Remove(id);
            History.MarkRead(e => e.Go == "chat:" + key);
            if (ChatOpen == key)
            {
                var group = before?.Conversations?.FirstOrDefault(c => c.Id == id)?.IsGroup ?? true;
                ChatOpen = null;
                TeamReplyTo = null;
                TeamEditing = null;
                DeleteAsk = null;
                Report = null;
                ShowSnack(group ? "This group was deleted." : "This chat was deleted.", [("OK", CloseSnack)]);
            }
        }
    }

    /// <summary>True for a conversation the service said was deleted (an old notice can still point at it).</summary>
    public bool IsGone(string? key) => ChatKeys.Parse(key) is ("conv", var id) && _goneSeen.Contains(id);

    /// <summary>What the backend refused from the outbox (an edit after 15 minutes, a day-off ask): shown once each.</summary>
    private void ShowNotices(ChatReply reply)
    {
        foreach (var n in reply.Notices ?? [])
        {
            if (_noticesShown.Add(n.Id) && n.Text.Length > 0)
            {
                ShowSnack(n.Text, [("OK", CloseSnack)]);
            }
        }
    }

    // ---- Ask for a day off ----------------------------------------------------------------------------

    /// <summary>The service and the backend both have leave (a 1.4.0 service, or a backend without it, shows no button).</summary>
    public bool LeaveOn => Status?.ChatVersion is not null && _chat?.Leave is not null;

    /// <summary>The heartbeat's note for today on Home: a holiday or approved leave.</summary>
    public string? TodayNote => LeaveDays.TodayNote(Status?.WorkRules);

    /// <summary>The recent asks: the ones still being sent first, then the server's (the coming days, then the past).</summary>
    public IReadOnlyList<LeaveRow> LeaveRows
    {
        get
        {
            var today = DateOnly.FromDateTime(Now.LocalDateTime);
            var rows = new List<LeaveRow>();
            var sending = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var i in _outbox.PendingChat().Where(i => i.Action == OutboxReply.ChatLeave))
                {
                    if (MessageOutbox.ReadLeave(i) is { } ask && sending.Add(ask.Date))
                    {
                        rows.Add(new LeaveRow(LeaveDays.Label(new LeaveAsk(0, ask.Date, ask.Half), today), "Sending…", ask.Note, true));
                    }
                }
            }
            catch (Exception)
            {
                // the file is busy
            }
            foreach (var a in LeaveDays.Recent((_chat?.Leave ?? []).Where(a => !sending.Contains(a.Date)), today, Math.Max(0, 5 - rows.Count)))
            {
                rows.Add(new LeaveRow(LeaveDays.Label(a, today), LeaveDays.StatusLabel(a.Status), a.Note, false));
            }
            return rows;
        }
    }

    public void OpenLeave()
    {
        if (!LeaveOn)
        {
            return;
        }
        OpenSheet(Sheet.Leave);
        _ = RefreshChatAsync();
    }

    /// <summary>Send the ask (through the outbox: offline it goes when back). Null when sent, else what to tell the member.</summary>
    public string? AskLeave(DateOnly? day, bool half, string? note)
    {
        if (!LeaveOn)
        {
            return "Zinwe isn’t running, so this can’t be sent yet. Try again in a moment.";
        }
        if (LeaveDays.Check(day, DateOnly.FromDateTime(Now.LocalDateTime)) is { } problem)
        {
            return problem;
        }
        try
        {
            _outbox.AppendLeave(LeaveDays.Request(day!.Value, half, note));
        }
        catch (Exception)
        {
            return "That didn’t save. Try again.";
        }
        Sheet = Sheet.None;
        ShowSnack(LeaveDays.SentToast, [("OK", CloseSnack)]);
        _ = RefreshChatAsync();
        return null;
    }

    /// <summary>The admin answered an ask: a notice ("Ananya approved your day off on Thu, 12 Nov.").</summary>
    private void AnnounceLeave(ChatReply reply)
    {
        if (reply.Leave is not { } asks)
        {
            return;
        }
        var today = DateOnly.FromDateTime(Now.LocalDateTime);
        foreach (var a in asks)
        {
            var had = _leaveSeen.GetValueOrDefault(a.Id);
            _leaveSeen[a.Id] = a.Status;
            if (had is not LeaveAsk.Requested || a.Status is not (LeaveAsk.Approved or LeaveAsk.Declined) || Stage != Stage.App)
            {
                continue;
            }
            var when = LeaveDays.Label(a, today);
            Notify(a.Status == LeaveAsk.Approved ? $"{OwnerFirstStart} approved your day off" : $"{OwnerFirstStart} couldn’t give you that day off",
                when, "home", "Open", "leave:" + a.Id + ":" + a.Status);
        }
    }
}
