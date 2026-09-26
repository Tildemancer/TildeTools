using System;
using TildeTools.Modules.EmoteSplitter.Chat;

namespace TildeTools.Modules.EmoteSplitter.Sending;

// /r is resolved per line, so a tell mid-send redirects the rest
// Part 1 goes as /r, its echo names who got it, and the rest go as /tell Name@World
public sealed class ReplyPin
{
    public const int EchoTimeoutMs = 5000;

    // "/tell " + name (32) + "@" + world (16) - "/r", reserved at split time
    public const int HeaderAllowance = 6 + 32 + 1 + 16 - 2;

    private bool _sending;
    private string _firstBody = string.Empty;
    private long _sentAt;

    public string? Target { get; private set; }

    public bool AwaitingEcho { get; private set; }

    // Null holds it until part 1's echo arrives
    public string? Rewrite(string line)
    {
        if (!TryReplyBody(line, out var body))
            return line;

        if (Target != null)
            return $"/tell {Target} {body}";

        if (AwaitingEcho)
            return null;

        // Waiting either way: for part 1's echo, which arrives inside the send call, or for the timeout
        // Part 1 went and its echo was never read, so the rest can't be pinned and the timeout drops them
        AwaitingEcho = true;
        if (_firstBody.Length > 0 && body != _firstBody)
            return null;

        (_sending, _firstBody) = (true, body);
        return line;
    }

    public void Sent(string line, long nowMs)
    {
        _sending = false;

        if (Target != null || !TryReplyBody(line, out _))
            return;

        (AwaitingEcho, _sentAt) = (true, nowMs);
    }

    // Never sent means no echo, so stop waiting
    public void Throttled(string line)
    {
        if (TryReplyBody(line, out _))
            AwaitingEcho = false;
    }

    // Any echo inside part 1's send is ours
    // After it only one with part 1's text, or a tell sent meanwhile takes the rest
    public bool Echoed(string nameAtWorld, string text)
    {
        if (!AwaitingEcho)
            return false;

        if (!_sending && !string.Equals(text.Trim(), _firstBody.Trim(), StringComparison.Ordinal))
            return false;

        (Target, AwaitingEcho) = (nameAtWorld, false);
        return true;
    }

    public bool TimedOut(long nowMs) => AwaitingEcho && nowMs - _sentAt > EchoTimeoutMs;

    public void Reset() => (Target, AwaitingEcho, _sending, _firstBody) = (null, false, false, string.Empty);

    public static bool IsReplyHeader(string header) => ChannelCommands.NameOf(header) == ChannelCommands.Reply;

    private static bool TryReplyBody(string line, out string body)
    {
        var space = line.IndexOf(' ');
        body = space < 0 ? string.Empty : line[(space + 1)..];
        return space >= 0 && IsReplyHeader(line[..space]);
    }
}
