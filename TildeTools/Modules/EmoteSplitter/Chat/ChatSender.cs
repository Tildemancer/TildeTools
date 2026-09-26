using System;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace TildeTools.Modules.EmoteSplitter.Chat;

// ProcessChatBoxEntry is sig-scanned and can be null after a patch
// Calling it null crashes the game. Yikes!
internal static unsafe class ChatSender
{
    // Lets our own sends past our send hook
    [ThreadStatic] internal static bool Passthrough;

    internal static bool Available => UIModule.MemberFunctionPointers.ProcessChatBoxEntry != null;

    // Chat 2's <at:group,key> tags arrive as text and are encoded as Chat 2 would, unknown pairs dropped
    // ChatTwoModule.EncodeTags, set in Plugin
    internal static Func<byte[], byte[]>? EncodeTags;

    // Shift-click puts "<item>" in the box and the item in a store that lasts one send, so only part 1 linked
    // Plain data bar the vtable, so safe to copy
    // Not LinkedItemName: it's a Utf8String owning a heap buffer
    private static AgentChatLog.LinkedInventoryItem _heldItem;

    internal static bool HoldingItemLink { get; private set; }

    // Call on the frame it was typed, while the copy is still the right one
    internal static void HoldItemLink()
    {
        var agent = AgentChatLog.Instance();
        HoldingItemLink = agent != null;

        if (HoldingItemLink)
            _heldItem = agent->LinkedItem;
    }

    internal static void ReleaseItemLink() => HoldingItemLink = false;

    private static void PutBackItemLink()
    {
        var agent = AgentChatLog.Instance();
        if (HoldingItemLink && agent != null)
            agent->LinkedItem = _heldItem;
    }

    // Framework thread only
    // No Available check: only the send queue calls this, and the module won't enable without it
    internal static void Send(string line, bool saveToHistory)
    {
        var bytes = Encoding.UTF8.GetBytes(line);

        // Only with a tag: the first encode builds Chat 2's whole list
        // Each tag it swaps leaves a zero byte at the end, which its own send takes as the terminator
        if (EncodeTags is { } encode && line.Contains("<at:", StringComparison.Ordinal))
            bytes = encode(bytes).AsSpan().TrimEnd((byte)0).ToArray();

        if (Array.IndexOf(bytes, (byte)0) >= 0)
            throw new InvalidOperationException("Message contained an embedded null byte.");

        byte[] buffer = [.. bytes, 0];

        Utf8String* message = null;
        try
        {
            fixed (byte* p = buffer)
                message = Utf8String.FromSequence(p);

            PutBackItemLink();

            Passthrough = true;
            UIModule.Instance()->ProcessChatBoxEntry(message, 0, saveToHistory);
        }
        finally
        {
            Passthrough = false;

            // On the game's heap, so the game's destructor frees it
            if (message != null)
                message->Dtor(true);
        }
    }

    // Reads the chat input's own flags
    // Chat 2 hardcodes 0x27F (Chat.cs:548, ChatBox.cs:37), which lacks the CJK bit and strips kana and kanji
    internal static string Sanitise(string text)
    {
        var input = ChatLogInput();

        // Plus the CJK bit
        var flags = input != null ? input->InputSanitizationFlags : (AllowedEntities)(0x27F | 0x400);

        var str = Utf8String.FromString(text);
        if (str == null)
            return text;

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
