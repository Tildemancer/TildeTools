using System;
using System.Text;
using Dalamud.Hooking;
using TildeTools.Modules.EmoteSplitter.Splitting;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace TildeTools.Modules.EmoteSplitter.Chat;

internal sealed unsafe class SubmitInterceptor : IDisposable
{
    private delegate void ProcessChatBoxEntryDelegate(UIModule* ui, Utf8String* message, nint a4, bool saveToHistory);

    private readonly Hook<ProcessChatBoxEntryDelegate> _hook;
    private readonly EmoteSplitterSettings _settings;
    private readonly Func<string, string, bool> _onSplit;
    private readonly Func<string, bool, bool> _onPlayerLine;

    // onSplit, onPlayerLine: true means it took the line
    // Built only while ChatSender.Available, see EmoteSplitterModule.UnavailableReason
    internal SubmitInterceptor(EmoteSplitterSettings settings, Func<string, string, bool> onSplit, Func<string, bool, bool> onPlayerLine)
    {
        _settings = settings;
        _onSplit = onSplit;
        _onPlayerLine = onPlayerLine;

        var address = (nint)UIModule.MemberFunctionPointers.ProcessChatBoxEntry;
        _hook = Svc.Interop.HookFromAddress<ProcessChatBoxEntryDelegate>(address, Detour);
        _hook.Enable();

        Svc.Log.Info($"Submit hook installed at 0x{address:X}.");
    }

    private void Detour(UIModule* ui, Utf8String* message, nint a4, bool saveToHistory)
    {
        try
        {
            if (!ChatSender.Passthrough && message != null && message->ToString() is { Length: > 0 } line)
            {
                if (ShouldSplit(line, saveToHistory, out var header, out var body, out var marked) && _onSplit(header, body))
                    return;

                if (marked)
                    Svc.Chat.PrintError("[Emote Splitter] That message went out whole, break markers and all. /xllog says why.");

                if (_onPlayerLine(line, saveToHistory))
                    return;
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Interceptor failed; passing the message through untouched.");
        }

        // Called once: a throw from a hook further down can come after the line went out
        try
        {
            _hook.Original(ui, message, a4, saveToHistory);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Passing the message through failed.");
        }
    }

    private bool ShouldSplit(string line, bool saveToHistory, out string header, out string body, out bool marked)
    {
        var budget = _settings.Budget;
        var bytes = Encoding.UTF8.GetByteCount(line);

        // One that fits still splits where the player put a break
        // Not a chat channel, like /t <t>: searched whole, only for the went-out-whole notice
        var splittable = ChannelCommands.TrySplittable(line, out header, out body);
        marked = MessageSplitter.FindBreak(splittable ? body : line).At >= 0;

        if (bytes <= budget && !marked)
            return false;

        Svc.Log.Info($"Message to split submitted: {bytes} bytes, budget {budget}, break marker {marked}.");

        // A bare line that fits splits at a marker only from the game's box
        // Messenger's foray and party finder tells go bare around a target it set
        if (bytes <= budget && header.Length == 0 && !saveToHistory)
        {
            Svc.Log.Info("Not splitting: a bare line from another plugin.");
            return false;
        }

        if (ChannelCommands.HasPayload(line))
        {
            Svc.Log.Info("Not splitting: contains auto-translate or link payloads.");
            return false;
        }

        if (!splittable)
        {
            Svc.Log.Info($"Not splitting: {line.Split(' ', 2)[0]} is not a recognised chat channel.");
            return false;
        }

        Svc.Log.Info($"Splitting: channel \"{ChannelCommands.KeyOf(header)}\", body {Encoding.UTF8.GetByteCount(body)} bytes.");
        return true;
    }

    public void Dispose() => _hook.Dispose();
}
