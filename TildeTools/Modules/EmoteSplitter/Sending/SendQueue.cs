using System;
using System.Collections.Generic;
using System.Linq;
using TildeTools.Modules.EmoteSplitter.Chat;
using Part = (string Line, int Part, int Of, int WaitMs);

namespace TildeTools.Modules.EmoteSplitter.Sending;

public enum SendQueueState
{
    Idle,
    Sending,
    Held,

    // Suspect is set, nothing goes until Resume
    Asking,
}

public sealed class SendQueue
{
    // Part and Of count within the message
    // WaitMs is a pause the player asked for, the least time after PausedFrom: when queued, then the message's last part
    // So a typed line or cut-in in between doesn't stretch it
    private sealed class Message(string channel, List<Part> parts)
    {
        public readonly string Channel = channel;
        public readonly List<Part> Parts = parts;
        public bool Started;
        public bool Typed;
        public long PausedFrom;
    }

    private readonly List<Message> _messages = [];
    private long _heldUntil;

    private Message? _current;

    private (Message From, string Line, int Part, int Of)? _lastSent, _suspect;
    private long _lastSentAt = -MaxIntervalMs;
    private long _lastPartAt = -MaxIntervalMs;
    private long _lastTypedAt = -MaxIntervalMs;
    private bool _queueWentLast;

    public const int ThrottleClaimWindowMs = 3000;

    // Protection against ProcessChatBoxEntry skipping the rate limit
    public const int MinIntervalMs = 1500;
    public const int MaxIntervalMs = 30000;

    public int IntervalMs { get; set => field = Math.Clamp(value, MinIntervalMs, MaxIntervalMs); } = 1500;

    public int FreeIntervalMs { get; set; }

    public Func<bool> CanSend { get; set; } = () => true;

    public Action<string> Sender { get; set; } = _ => { };

    // Null holds for the next Update
    public Func<string, string?> Rewrite { get; set; } = line => line;

    public event Action<string, long>? LineSent;

    public event Action<Exception>? SendFailed;

    // (part, of), within the sent line's own message
    public event Action<int, int>? Progress;

    public event Action? Finished;

    // A throttle notice came right after this line, which may or may not have posted
    public event Action<string>? Suspected;

    // Before a message's first line is rewritten, and again when it picks up after a cut-in
    public event Action? MessageStarting;

    public int PendingCount => _messages.Sum(message => message.Parts.Count);

    public string? Head => _messages.Count > 0 ? _messages[0].Parts[0].Line : null;

    public (int Part, int Of)? HeadPart => _messages.Count > 0 ? (_messages[0].Parts[0].Part, _messages[0].Parts[0].Of) : null;

    public string? Suspect => _suspect?.Line;

    public bool LastSentWasTyped => _lastSent?.From.Typed == true;

    // The channel of the message a cut-in goes in front of: the last started one still listed
    public string? Underway => _messages.FindLast(message => message.Started)?.Channel;

    public string? Current => _current is { } message && _messages.Contains(message) ? message.Channel : null;

    public SendQueueState State =>
        _suspect != null ? SendQueueState.Asking
        : _messages.Count == 0 ? SendQueueState.Idle
        : CanSend() ? SendQueueState.Sending
        : SendQueueState.Held;

    // ahead: in front of the message partway through, which picks up again after
    // typed: one line's worth the player sent, split only at their markers
    public void Enqueue(
        IEnumerable<string> lines, string channel = "", bool ahead = false, IReadOnlyList<int>? pausesMs = null, long nowMs = 0, bool typed = false)
    {
        var parts = lines.ToList();
        if (parts.Count == 0)
            return;

        if (_messages.Count == 0)
            _lastSent = null;

        var (at, asTyped) = Place(channel, ahead, typed);
        _messages.Insert(at, new Message(channel, parts.Select((line, i) => (line, i + 1, parts.Count, pausesMs?.ElementAtOrDefault(i) ?? 0)).ToList())
        {
            PausedFrom = nowMs,
            Typed = asTyped,
        });
    }

    // Never past a message that might share its channel, so each channel reads in the order sent
    // Except one line's worth typed with anything queued, which goes next as a held line does, after the lines typed before it
    private (int At, bool Typed) Place(string channel, bool ahead, bool typed)
    {
        if (typed && ahead && _messages.Count > 0 && TypedAt is { } next)
            return (next, true);

        var underway = _messages.FindLastIndex(message => message.Started);

        return (ahead && underway >= 0 && _messages.FindIndex(underway, message => ChannelCommands.MightShare(message.Channel, channel)) < 0
            ? underway
            : _messages.Count, false);
    }

    // After the lines typed before it, null in front of a reply underway
    private int? TypedAt => _messages.FindLastIndex(message => message.Typed) + 1 is var at
        && !(at < _messages.Count && _messages[at] is { Started: true, Channel: ChannelCommands.Reply }) ? at : null;

    public bool GoesNext(string channel, bool ahead, bool typed = false) => _suspect == null && Place(channel, ahead, typed).At == 0;

    // A line the player sent on a channel with the game's wait
    // Mid-post and under MinIntervalMs after the last line, it's held to go next, and the post carries on after
    // Behind a typed line still held, whatever the time, so they go in the order typed
    // Otherwise it's gone out, and the next part waits MinIntervalMs after it
    public bool Typed(string line, string channel, long nowMs, bool canHold)
    {
        var posting = _queueWentLast || _messages.Any(message => !ChannelCommands.Unlimited(message.Channel));
        var tooSoon = nowMs < Math.Max(_lastPartAt, _lastTypedAt) + MinIntervalMs;

        // Held or not, ours is no longer the last line to a notice
        _queueWentLast = false;

        if (canHold && TypedAt is { } at && (at > 0 || _suspect == null && posting && tooSoon))
        {
            _messages.Insert(at, new Message(channel, [(line, 1, 1, 0)]) { Typed = true });
            return true;
        }

        _lastTypedAt = nowMs;
        return false;
    }

    public void Cancel()
    {
        _messages.Clear();
        (_lastSent, _suspect) = (null, null);
    }

    // The rest of the message posting now, wherever cut-ins have put it
    // Returns how many unsent lines it dropped
    public int DropCurrentMessage()
    {
        // Nothing dropped: the question is about a message that finished, so it stays
        if (_current is not { } message || !_messages.Remove(message))
            return 0;

        // A late throttle notice for a dropped line mustn't bring it back
        (_lastSent, _suspect) = (null, null);
        return message.Parts.Count;
    }

    public void ReportThrottled(long nowMs)
    {
        // The notice fires for anything sent too fast, so only one within ThrottleClaimWindowMs counts as ours
        // Claimed later, posting would pause and offer to resend a part from god only knows how long ago. imagine getting rate limited and posting your last spicy rp in novice network. BIG yikes.
        if (_lastSent is not { } sent || nowMs - _lastSentAt > ThrottleClaimWindowMs)
            return;

        // A line the player sent after ours went last, so the notice is about that one
        // Nor is it about free company and the like, which have no wait to break
        if (!_queueWentLast || ChannelCommands.Unlimited(sent.From.Channel))
            return;

        (_suspect, _lastSent) = (sent, null);

        Suspected?.Invoke(sent.Line);
    }

    // resend puts the suspect back in front of the rest of its message, otherwise it counts as posted
    public void Resume(bool resend)
    {
        if (_suspect is not { } suspect)
            return;

        _suspect = null;
        if (!resend)
            return;

        var part = (suspect.Line, suspect.Part, suspect.Of, 0);

        // Its message finished: the line alone goes first, nothing has posted since
        if (!_messages.Contains(suspect.From))
            _messages.Insert(0, new Message(suspect.From.Channel, [part]) { Typed = suspect.From.Typed });
        else
            suspect.From.Parts.Insert(0, part);
    }

    public void Update(long nowMs)
    {
        if (_messages.Count == 0 || _suspect != null || nowMs < DueAt)
            return;

        if (!CanSend())
        {
            // Keep moving the wait so the backlog doesn't burst out
            _heldUntil = nowMs + IntervalMs;
            return;
        }

        if (NextLine() is { } line)
            SendHead(line, nowMs);
    }

    // Outside ChannelCommands.Unlimited it runs from the last part on any of them, which share the game's wait
    // A part keeps the post's own pace, and only needs MinIntervalMs after a typed line
    // Unlimited: FC, party and linkshells took parts a frame apart, every one arriving
    private long DueAt => Math.Max(Math.Max(_heldUntil, _messages[0].PausedFrom + _messages[0].Parts[0].WaitMs), _messages[0] switch
    {
        var head when ChannelCommands.Unlimited(head.Channel) => _lastSentAt + FreeIntervalMs,
        { Typed: true } => Math.Max(_lastPartAt, _lastTypedAt) + MinIntervalMs,
        _ => Math.Max(_lastPartAt + IntervalMs, _lastTypedAt + MinIntervalMs),
    });

    private string? NextLine()
    {
        var message = _messages[0];

        if (message != _current)
        {
            (_current, message.Started) = (message, true);
            MessageStarting?.Invoke();
        }

        return Rewrite(message.Parts[0].Line);
    }

    private void SendHead(string line, long nowMs)
    {
        var message = _messages[0];
        var part = message.Parts[0];

        message.Parts.RemoveAt(0);
        if (message.Parts.Count == 0)
            _messages.RemoveAt(0);

        // As sent, so a resend repeats a rewritten /tell rather than the /r
        // Before sending: a refusal raised inside the call drops the message and clears this, which has to stick
        _lastSent = (message, line, part.Part, part.Of);
        (_lastSentAt, message.PausedFrom) = (nowMs, nowMs);

        var limited = !ChannelCommands.Unlimited(message.Channel);
        if (limited && message.Typed)
            _lastTypedAt = nowMs;
        else if (limited)
            _lastPartAt = nowMs;

        _queueWentLast |= limited;

        try
        {
            Sender(line);
        }
        catch (Exception ex)
        {
            // Partial message < None
            Cancel();
            SendFailed?.Invoke(ex);
            return;
        }

        // Dropped or cancelled inside the call, and the batch has already ended
        if (_lastSent is null && _suspect is null)
            return;

        LineSent?.Invoke(line, nowMs);

        Progress?.Invoke(part.Part, part.Of);

        if (_messages.Count == 0)
            Finished?.Invoke();
    }
}
