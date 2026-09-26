using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using TildeTools.Modules.EmoteSplitter.Chat;
using TildeTools.Modules.EmoteSplitter.Sending;
using TildeTools.Modules.EmoteSplitter.Splitting;
using Chunks = System.Collections.Generic.IReadOnlyList<TildeTools.Modules.EmoteSplitter.Splitting.SplitPart>;

namespace TildeTools.Modules.EmoteSplitter;

internal sealed class EmoteSplitterModule : IModule
{
    private readonly EmoteSplitterSettings _settings;
    private readonly Action _save;
    private readonly Action _announce;
    private readonly SendQueue _queue = new();
    private readonly ReplyPin _pin = new();
    private readonly SettingsTab _tab;

    private SubmitInterceptor? _submit;
    private EnterInterceptor? _enter;
    private InputCapManager? _inputCap;

    public string Id => "emote-splitter";

    public string Name => "Emote Splitter";

    public string Description => "Type past the chat limit. Long messages are split and sent in order.";

    public bool IsEnabled { get; private set; }

    public string? UnavailableReason => ChatSender.Available
        ? null
        : "The game's chat-send function could not be found for this game version.";

    private static long NowMs => Environment.TickCount64;

    // Wired once, the posting window too: off, the queue is empty and nothing calls Update
    internal EmoteSplitterModule(EmoteSplitterSettings settings, Action save, Action announce, WindowSystem windows)
    {
        _settings = settings;
        _save = save;
        _announce = announce;
        _tab = new SettingsTab(settings, OnSettingsChanged);
        windows.AddWindow(new PostingWindow(_queue, _pin, Stop));

        _queue.Sender = line => ChatSender.Send(line, saveToHistory: _toHistory.Remove(line));
        _queue.Rewrite = _pin.Rewrite;
        // GPose sets WatchingCutscene, as Chat 2's GposeActive reads it, and chat posts there
        _queue.CanSend = () => Svc.InWorld || Svc.ClientState.IsGPosing;
        _queue.Progress += OnProgress;
        _queue.Finished += OnFinished;
        _queue.SendFailed += OnSendFailed;
        _queue.Suspected += OnSuspected;
        _queue.LineSent += _pin.Sent;
        _queue.MessageStarting += _pin.Reset;
    }

    public void Enable()
    {
        _queue.IntervalMs = _settings.IntervalMs;
        _queue.FreeIntervalMs = _settings.FreeIntervalMs;

        // The first pass: every line, handled later or not, and the sender before another plugin's rename is written back
        Svc.Chat.ChatMessage += OnChatMessage;
        Svc.Chat.LogMessage += OnLogMessage;
        Svc.ClientState.Logout += OnLogout;
        Svc.ClientState.Login += OnLogin;

        _inputCap = new InputCapManager(_settings);
        _submit = new SubmitInterceptor(_settings, (header, body) => OnMessageNeedsSplitting(header, body), OnPlayerLine);
        _enter = new EnterInterceptor(OnEnteredLine);

        Svc.Framework.Update += OnFrameworkUpdate;

        // The first split compiles the splitter, about 25 ms on the frame of the first long paste
        Task.Run(WarmUp);

        IsEnabled = true;

        if (!InputCapManager.Available && _settings.UnlockChatInput)
            Svc.Chat.PrintError("[Emote Splitter] The chat box's length limit could not be raised for this game version. Splitting still works if you paste into the box.");
    }

    // Off the game's thread: no game calls, so no Sanitise or channel pin, just the pure path they feed
    private void WarmUp()
    {
        var line = "/s " + string.Join(' ', Enumerable.Repeat("A warm-up sentence, long enough to need splitting.", 40));

        try
        {
            ChannelCommands.TrySplittable(line, out var header, out var body);

            var options = _settings.ToSplitOptions();
            var chunks = MessageSplitter.SplitWithBodies(header, _settings.DetachOoc(body, options), options);
            BodySources.Locate(line, chunks, line.Length - body.Length);
        }
        catch (Exception ex)
        {
            Svc.Log.Debug(ex, "Splitter warm-up failed; the first split just runs cold.");
        }
    }

    public void Disable()
    {
        Svc.Framework.Update -= OnFrameworkUpdate;

        DropBatch();

        Svc.Chat.ChatMessage -= OnChatMessage;
        Svc.Chat.LogMessage -= OnLogMessage;
        Svc.ClientState.Logout -= OnLogout;
        Svc.ClientState.Login -= OnLogin;

        // Reverse of Enable
        _enter?.Dispose();
        _submit?.Dispose();
        _inputCap?.Dispose();

        (_enter, _submit, _inputCap) = (null, null, null);
        (_replyTo, _droppedAtLogout, _previewed, IsEnabled) = (null, 0, null, false);
    }

    public void DrawTab() => _tab.Draw();

    // From the preview split, so it's the channel the parts go to: a /r's recipient or a bare line's pin
    internal int PostingMsForIpc(string line)
    {
        if (Previewed(line) is not { Count: > 1 } chunks
            || !ChannelCommands.TrySplittable(chunks[0].Line, out var header, out _))
            return 0;

        var gap = ChannelCommands.Unlimited(ChannelCommands.KeyOf(header)) ? _settings.FreeIntervalMs : _settings.IntervalMs;
        var pauses = PausesOf(chunks);
        return pauses[0] + pauses.Skip(1).Sum(pause => Math.Max(gap, pause));
    }

    private static int[] PausesOf(Chunks chunks) =>
        [.. chunks.Select(chunk => Math.Min(chunk.Pause, SendQueue.MaxIntervalMs / 1000) * 1000)];

    internal List<int> SplitBodySpansForIpc(string line) =>
        Previewed(line) is { } chunks ? [.. chunks.SelectMany(c => new[] { c.BodyStart, c.BodyLength })] : [];

    // The body is the line's tail, so its length gives the header's
    // See BodySources.Locate
    internal List<int> SplitBodySourcesForIpc(string line) => Previewed(line) is { } chunks
        ? [.. BodySources.Locate(line, chunks, ChannelCommands.TrySplittable(line, out _, out var body) ? line.Length - body.Length : 0)]
        : [];

    internal List<string> SplitForIpc(string line) =>
        Previewed(line) is { } chunks ? [.. chunks.Select(c => c.Line)] : [];

    private (string Line, string Pinned, string? ReplyTo, Chunks Chunks)? _previewed;

    // Chat 2 and Wordsmith ask four gates per keystroke, and one split serves them all
    // Cleared by a settings change or switching off
    private Chunks? Previewed(string line)
    {
        // A bare line goes to the chat box's channel and a /r to whoever last sent a tell, so both are in the key
        var pinned = string.Empty;
        ActiveChannel.TryPin(ref pinned);

        if (_previewed is { } kept && kept.Pinned == pinned && kept.ReplyTo == _replyTo && kept.Line == line)
            return kept.Chunks;

        // Any length, so a caller's preview matches the later send
        if (!TryPrepare(line, requireSplit: false, out var chunks, out var reason, out _))
        {
            if (reason != null)
                Svc.Log.Debug($"IPC split declined: {reason}");

            return null;
        }

        _previewed = (line, pinned, _replyTo, chunks);
        return chunks;
    }

    internal SplitTake SendStatusForIpc(string line, bool requireSplit = true)
    {
        if (!TryPrepare(line, requireSplit, out var chunks, out var reason, out var fits))
        {
            if (reason == null)
                return SplitTake.NotTaken;

            Refuse(reason);
            return SplitTake.Refused;
        }

        // Before the hold below, which would count as the message being posted holding one
        var ahead = CanCutIn;
        if (CantWait(chunks, ahead, fits) is { } wait)
        {
            Refuse(wait);
            return SplitTake.Refused;
        }

        if (line.Contains("<item>", StringComparison.Ordinal))
        {
            if (ChatSender.HoldingItemLink && _queue.State != SendQueueState.Idle)
            {
                Refuse("That message links an item while the one still posting links its own, and only one can be held. " +
                       "Nothing was sent. Send it again once the Emote Splitter window has closed.");
                return SplitTake.Refused;
            }

            ChatSender.HoldItemLink();
        }

        Queue(chunks, "from another plugin", ahead, fits);
        return SplitTake.Queued;
    }

    // False with no reason: not ours to split
    private bool TryPrepare(string line, bool requireSplit, out Chunks chunks, out string? reason, out bool fits)
    {
        (chunks, reason, fits) = ([], null, false);

        if (!IsEnabled || string.IsNullOrWhiteSpace(line))
            return false;

        var splittable = ChannelCommands.TrySplittable(line, out var header, out var body);

        fits = Encoding.UTF8.GetByteCount(line) <= _settings.Budget;
        var splits = !fits
            || splittable && MessageSplitter.FindBreak(body).At >= 0;

        if (requireSplit && !splits)
            return false;

        if (ChannelCommands.HasPayload(line))
        {
            reason = PayloadRefusal;
            return false;
        }

        return splittable && TrySplit(header, body, splits, out chunks, out reason);
    }

    private const string PayloadRefusal =
        "That message has to be split, and it contains an auto-translate phrase or item link, " +
        "which splitting would corrupt. Nothing was sent.";

    private bool TrySplit(string header, string body, bool splits, out Chunks chunks, out string? reason)
    {
        (chunks, reason) = ([], null);

        // A /r goes to whoever last sent a tell, even from before a restart (tested 2026-09-24)
        // Known, the parts go to them as tells; unknown, ReplyPin works it out from part 1's echo
        // A bare line pins to the box's channel now, so a later switch can't move it
        var pinned = ReplyPin.IsReplyHeader(header) && _replyTo is { } to ? $"/tell {to}" : header;
        if (!ActiveChannel.TryPin(ref pinned) && splits)
        {
            reason = ActiveChannel.Unreadable;
            return false;
        }

        // One that fits stays a /r, or bare, when the command would push it over the budget or the channel can't be read
        if (splits || Encoding.UTF8.GetByteCount($"{pinned} {body}") <= _settings.Budget)
            header = pinned;

        var options = _settings.ToSplitOptions();
        options.MaxBytes = _settings.Budget;

        // Room for the /tell Name@World every part after the first becomes
        // Only a /r that splits gets rewritten
        options.SafetyMargin = splits && ReplyPin.IsReplyHeader(header) ? ReplyPin.HeaderAllowance : 0;

        try
        {
            // Sanitised before measuring, so the budget matches what's sent
            chunks = MessageSplitter.SplitWithBodies(header, _settings.DetachOoc(ChatSender.Sanitise(body), options), options);
        }
        catch (SplitBudgetException ex)
        {
            reason = $"Could not split that message: {ex.Message}";
            return false;
        }

        if (chunks.Count == 0)
        {
            reason = "That message is only break markers, with no text to send. Nothing was sent.";
            return false;
        }

        if (chunks.Count <= _settings.MaxChunksPerMessage)
            return true;

        reason = $"That message needs {chunks.Count} parts, over the limit of {_settings.MaxChunksPerMessage}. " +
                 "Nothing was sent. Raise the limit in /tt if you meant it.";
        chunks = [];
        return false;
    }

    private static void Refuse(string reason)
    {
        Svc.Log.Info($"Refused: {reason}");
        Svc.Chat.PrintError($"[Emote Splitter] {reason}");
    }

    private void Queue(Chunks chunks, string how, bool ahead, bool typed)
    {
        ChannelCommands.TrySplittable(chunks[0].Line, out var header, out _);
        var channel = ChannelCommands.KeyOf(header);
        Svc.Log.Info($"Queued {chunks.Count} chunk(s) {how}, channel \"{channel}\", ahead {ahead}, typed {typed}.");
        _queue.Enqueue(chunks.Select(c => c.Line), channel, ahead, PausesOf(chunks), NowMs, typed);

        if (_settings.ShowProgress && chunks.Count > 1)
            Svc.Chat.Print($"[Emote Splitter] Sending {chunks.Count} parts...");
    }

    // Not in front of a /r, whose pin resets when the new message starts, nor of a held item link or an open question
    private bool CanCutIn =>
        _queue.State != SendQueueState.Asking && !ChatSender.HoldingItemLink && _queue.Underway != ChannelCommands.Reply;

    private string? CantWait(Chunks chunks, bool ahead, bool typed)
    {
        if (!ChannelCommands.TrySplittable(chunks[0].Line, out var header, out _) || !ReplyPin.IsReplyHeader(header))
            return null;

        if (!_queue.CanSend())
            return ReplyCantWait + "for a loading screen or cutscene to end. Nothing was sent. Send it again once it has.";

        if (chunks[0].Pause > 0)
            return ReplyCantWait + "out a pause at its start. Nothing was sent. Send it without one.";

        return _queue.GoesNext(ChannelCommands.KeyOf(header), ahead, typed)
            ? null
            : ReplyCantWait + "behind another message. Nothing was sent. Send it again once the Emote Splitter window has closed.";
    }

    private const string ReplyCantWait =
        "A reply goes to whoever last sent you a tell when its first part is posted, so it can't wait ";

    internal void Stop() => Svc.Chat.Print($"[Emote Splitter] Stopped; {DropBatch()} part(s) not sent.");

    private void OnSettingsChanged()
    {
        _previewed = null;
        _queue.IntervalMs = _settings.IntervalMs;
        _queue.FreeIntervalMs = _settings.FreeIntervalMs;
        _save();
        _inputCap?.Apply();
    }

    private bool OnEnteredLine(string line, byte[] raw)
    {
        var bytes = Encoding.UTF8.GetByteCount(line);
        var payload = ChannelCommands.HasPayload(line);
        var splittable = ChannelCommands.TrySplittable(line, out var header, out var body);

        // One that fits too with a break marker, so a refusal says why and puts it back
        // Not with a link or auto-translate phrase, which the game sends whole and taking it would drop
        if (bytes <= _settings.Budget && (payload || !splittable || MessageSplitter.FindBreak(body).At < 0))
            return false;

        Svc.Log.Info($"Enter on a line to split: {bytes} bytes, budget {_settings.Budget}.");

        // Taken, not handed back: over the budget, the game drops it silently
        if (payload)
        {
            Refuse(PayloadRefusal);
            _refused = raw;
            return true;
        }

        if (!splittable)
        {
            Svc.Log.Info("Leaving it alone: not a chat channel.");
            return false;
        }

        return OnMessageNeedsSplitting(header, body, raw);
    }

    // A line the player sent that isn't split: the queue holds it to go next when it would land too soon mid-post
    private bool OnPlayerLine(string line, bool saveToHistory)
    {
        if (!ChannelCommands.TrySplittable(line, out var header, out var body))
        {
            // A tell whose target isn't a name, like /t <t>, still shares the wait
            if (ChannelCommands.IsTell(line))
                _queue.Typed(line, ChannelCommands.Tell, NowMs, canHold: false);

            return false;
        }

        // Held, it goes where it would now: a /r to the last teller, a bare line to the box's channel
        if (ReplyPin.IsReplyHeader(header) && _replyTo is { } to)
            header = $"/tell {to}";

        // Bare only from the game's own box, which saves history: Messenger sends bare lines around a tell target it set
        // ExtraChat's channels pin to nothing, and a bare line held would go wherever the box is by then
        if (header.Length == 0 && saveToHistory)
            ActiveChannel.TryPin(ref header);

        var pinned = header.Length > 0 && !ReplyPin.IsReplyHeader(header);

        var channel = ChannelCommands.KeyOf(header);
        if (ChannelCommands.Unlimited(channel))
            return false;

        // Not held: a link's bytes and an <item> wouldn't survive a later send
        // Nor one the added command takes past 500 bytes
        var held = header.Length > 0 ? $"{header} {body}" : body;
        var canHold = pinned && !ChannelCommands.HasPayload(line)
            && !line.Contains("<item>", StringComparison.Ordinal)
            && Encoding.UTF8.GetByteCount(held) <= EmoteSplitterSettings.MaxChunkBytes;

        if (!_queue.Typed(held, channel, NowMs, canHold))
            return false;

        if (saveToHistory)
            _toHistory.Add(held);

        Svc.Log.Info($"Held a line typed mid-post to go next, channel \"{channel}\".");
        return true;
    }

    private readonly HashSet<string> _toHistory = new(ReferenceEqualityComparer.Instance);

    // Null when another plugin sent it
    private bool OnMessageNeedsSplitting(string header, string body, byte[]? putBack = null)
    {
        var whole = Encoding.UTF8.GetByteCount(header.Length > 0 ? $"{header} {body}" : body);
        var fits = whole <= _settings.Budget;

        var ahead = CanCutIn;
        if (TrySplit(header, body, splits: true, out var chunks, out var reason)
            && (reason = CantWait(chunks, ahead, fits)) == null)
        {
            Queue(chunks, "through the send hook", ahead, fits);
            return true;
        }

        // Another plugin's line can't go back in its box, so one the game takes whole goes as it is
        if (putBack == null && whole <= EmoteSplitterSettings.MaxChunkBytes)
        {
            Svc.Log.Info($"Sent whole: {reason}");
            return false;
        }

        Refuse(reason!);
        _refused = putBack;
        return true;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        PutBackRefusedText();
        DropUnconfirmedReply();
        _queue.Update(NowMs);

        // A bare line takes the chat box's channel, so Wordsmith's None pad splits again when it moves
        if (ActiveChannel.Fingerprint() is var channel && channel != _channelSeen)
        {
            _channelSeen = channel;
            _announce();
        }

        if (_finished is { } done && _queue.State == SendQueueState.Idle && NowMs - done.At > SendQueue.ThrottleClaimWindowMs)
        {
            EndBatch();

            if (done.Say)
                Svc.Chat.Print("[Emote Splitter] Message sent.");
        }
    }

    private int _channelSeen;

    private byte[]? _refused;

    // Taking the Enter clears the box either way, so a refused message goes back a frame later
    private unsafe void PutBackRefusedText()
    {
        if (_refused is not { } text)
            return;

        _refused = null;

        var input = ChatSender.ChatLogInput();
        if (input != null)
            fixed (byte* bytes = text)
                input->SetText(bytes);
    }

    // Every way a batch ends comes through here, so the pin and the held item always go with it
    private void EndBatch()
    {
        _pin.Reset();
        ChatSender.ReleaseItemLink();
        _toHistory.Clear();
        _finished = null;
    }

    private int _droppedAtLogout;

    // What's left would post from whichever character logs in next, a pinned /tell included
    private void OnLogout(int type, int code)
    {
        _droppedAtLogout += DropBatch();
        ReplyTo(null);
    }

    // Said now, the chat log at logout is gone before anyone reads it
    private void OnLogin()
    {
        if (_droppedAtLogout == 0)
            return;

        Svc.Chat.Print($"[Emote Splitter] You logged out mid-post, so {_droppedAtLogout} part(s) weren't sent.");
        _droppedAtLogout = 0;
    }

    private int DropBatch()
    {
        var dropped = _queue.PendingCount;
        _queue.Cancel();
        EndBatch();
        return dropped;
    }

    // The message being sent, not those queued behind it
    // The pin resets now, or TimedOut drops the next one as well
    private int DropMessage()
    {
        var dropped = _queue.DropCurrentMessage();

        if (_queue.PendingCount == 0)
            EndBatch();
        else
            _pin.Reset();

        return dropped;
    }

    private void OnProgress(int part, int of)
    {
        Svc.Log.Info($"Sent chunk {part}/{of}.");

        // A reply's last part, so a /r queued behind it isn't shown going to the same person
        if (part == of)
            _pin.Reset();
    }

    private (long At, bool Say)? _finished;

    // "Not heard" can still ask about the last part for ThrottleClaimWindowMs, and posting it again needs its item link: see OnFrameworkUpdate
    // A typed line sent last keeps the Say its post's own Finished set
    private void OnFinished() =>
        _finished = (NowMs, _settings.ShowProgress && (!_queue.LastSentWasTyped || _finished?.Say == true));

    private void OnSendFailed(Exception ex)
    {
        EndBatch();
        Svc.Log.Error(ex, "Send failed; the rest of the message was dropped.");
        Svc.Chat.PrintError($"[Emote Splitter] Send failed: {ex.Message}");
    }

    private void OnSuspected(string line)
    {
        _pin.Throttled(line);

        Svc.Log.Warning("Throttle notice right after a part; paused to ask.");
        Svc.Chat.Print("[Emote Splitter] Paused. The game says a message wasn't heard, so the Emote Splitter window asks whether to post the last part again.");
    }

    private void OnLogMessage(ILogMessage message)
    {
        if (message.LogMessageId == LogMessages.Throttled && _settings.RetryOnThrottle)
            _queue.ReportThrottled(NowMs);
        else if (LogMessages.Fatal.Contains(message.LogMessageId))
            OnFatalRejection(message.LogMessageId);
    }

    private void OnFatalRejection(uint logMessageId)
    {
        // Null when it was about a message already sent in full
        // Its last part may be what was refused, so no "Message sent." for it
        if (_queue.Current is not { } current)
        {
            if (_finished is { } done)
                _finished = (done.At, false);

            return;
        }

        // Only ours when the message being posted goes to the channel it's about, and "" could be any: see MightShare
        if (LogMessages.ChannelOf(logMessageId) is { } channel && current.Length > 0 && !current.Contains(channel))
            return;

        var dropped = DropMessage();
        Svc.Log.Warning($"Chat refused (LogMessage {logMessageId}); dropped {dropped} queued chunk(s).");
        Svc.Chat.PrintError(
            $"[Emote Splitter] The game refused the message, so the remaining {dropped} part(s) " +
            "were not sent. See the error above this line for the reason.");
    }

    private string? _replyTo;

    // An outgoing tell's sender field holds the recipient
    // See Messenger's DecodeSender
    private void OnChatMessage(IChatMessage message)
    {
        var kind = message.LogKind;

        if (kind is not (XivChatType.TellOutgoing or XivChatType.TellIncoming))
            return;

        string? person = null;

        try
        {
            // Original, as the echo is: a plugin renaming senders can drop the PlayerPayload
            if (message.OriginalSender.ToDalamudString().Payloads.OfType<PlayerPayload>().FirstOrDefault() is { } player)
            {
                var world = player.World.ValueNullable?.Name.ToString();
                if (string.IsNullOrEmpty(world))
                    Svc.Log.Warning($"A tell named no world (row {player.World.RowId}).");
                else
                    person = $"{player.PlayerName}@{world}";
            }

            // One in that can't be read still moved /r, so it's unknown until the next one or an echo
            if (kind == XivChatType.TellIncoming)
                ReplyTo(person);
            else if (person != null && _pin.Echoed(person, message.OriginalMessage.ExtractText()))
            {
                Svc.Log.Info("Reply batch pinned to its recipient.");
                ReplyTo(person);
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Could not read who a tell was with.");

            if (kind == XivChatType.TellIncoming)
                ReplyTo(null);
        }
    }

    // Chat 2 splits its preview again on the announcement, so a /r there names who it goes to now
    private void ReplyTo(string? person)
    {
        if (person == _replyTo)
            return;

        _replyTo = person;
        _announce();
    }

    // No echo: offline or nobody to reply to
    // The rest could land on anyone
    private void DropUnconfirmedReply()
    {
        if (!_pin.TimedOut(NowMs))
            return;

        var dropped = DropMessage();

        Svc.Log.Warning($"No echo for the first /r part; dropped {dropped} queued chunk(s).");
        Svc.Chat.PrintError(
            $"[Emote Splitter] Could not confirm who the reply went to, so the remaining {dropped} " +
            "part(s) were not sent. Use /tell Name@World to send them.");
    }

    public void Dispose()
    {
        if (IsEnabled)
            Disable();
    }
}
