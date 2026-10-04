using System;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Text.ReadOnly;

namespace TildeTools.Modules.EmoteSplitter.Chat;

internal static unsafe class ChatSender
{
    // Lets our own sends past our send hook.
    [ThreadStatic] internal static bool Passthrough;

    // C2's <at:group,key> tags arrive as text and get encoded the way C2 would, dropping unknown pairs.
    // ChatTwoModule.EncodeTags, set in Plugin.
    internal static Func<byte[], byte[]>? EncodeTags;

    // Framework thread only.
    internal static void Send(string line)
    {
        // Only when there's a tag.
        // The first encode builds a list of every tag C2 has.
        // Each tag it swaps leaves a zero byte at the end, which its own send treats as the terminator.
        ReadOnlySpan<byte> bytes = EncodeTags is { } encode && line.Contains("<at:", StringComparison.Ordinal)
            ? encode(Encoding.UTF8.GetBytes(line)).AsSpan().TrimEnd((byte)0)
            : Encoding.UTF8.GetBytes(line);

        if (bytes.Contains((byte)0))
            throw new InvalidOperationException("Message contained an embedded null byte.");

        // IN THEORY, this is redundant. since the splitter, the hold check and the refusals already keep every line at 500 bytes or under; nothing should ever reach it and if this ever fires, something has gone horribly wrong.
        // THAT SAID, defensive design and all, I'm electing to keep it even if it's clumsy, even if it sucks ass, even if it's completely impossible to trigger, because the stakes of not having this or something going wrong are just too high. Today's little 1kb experiment has put the fear of God into me, so this is going to be a peace of mind indulgence.
        if (bytes.Length > EmoteSplitterSettings.MaxChunkBytes)
        {
            Svc.Log.Error($"Stopped a {bytes.Length}-byte line before it went out.");
            return;
        }

        byte[] buffer = [.. bytes, 0];

        Utf8String* message = null;
        try
        {
            fixed (byte* p = buffer)
                message = Utf8String.FromSequence(p);

            Passthrough = true;
            UIModule.Instance()->ProcessChatBoxEntry(message, 0, false);
        }
        finally
        {
            Passthrough = false;

            // The game destructor has to handle this since it's on the game's heap.
            if (message != null)
                message->Dtor(true);
        }
    }

    // EVERY payload counts, INCLUDING broken ones.
    // It's the bytes that matter here, not the macro type.
    internal static bool HasPayload(ReadOnlySpan<byte> raw)
    {
        foreach (var payload in new ReadOnlySeStringSpan(raw))
            if (payload.Type != ReadOnlySePayloadType.Text)
                return true;

        return false;
    }

    // Null terminated bytes from the box.
    // From EnterInterceptor as it passes them.
    internal static void SaveToHistory(byte[] raw, AtkComponentTextInput* input)
    {
        Utf8String* line;
        fixed (byte* p = raw)
            line = Utf8String.FromSequence(p);

        // The history keeps its own copy, as it does of the box's own lines, so ours can go.
        UIModule.Instance()->AddAtkHistoryEntry(line, input->AtkHistoryIndex);
        line->Dtor(true);
    }

    // Reads the chat input's flags.
    // C2 hardcodes 0x27F (Chat.cs:548, ChatBox.cs:37), which lacks the CJK bit, might strip kana and kanji for JP...
    internal static string Sanitize(string text)
    {
        var input = ChatLogInput();

        // Plus the CJK bit to address the above.
        var flags = input != null ? input->InputSanitizationFlags : (AllowedEntities)(0x27F | 0x400);

        var str = Utf8String.FromString(text);
        try
        {
            str->SanitizeString(flags);
            return str->ToString();
        }
        finally
        {
            str->Dtor(true);
        }
    }

    internal static AtkComponentTextInput* ChatLogInput() =>
        Svc.GameGui.GetAddonByName("ChatLog") is { IsNull: false } chatLog ? ((AddonChatLog*)chatLog.Address)->TextInput : null;
}
