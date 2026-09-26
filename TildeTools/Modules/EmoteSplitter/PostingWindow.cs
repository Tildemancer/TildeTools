using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using TildeTools.Modules.EmoteSplitter.Chat;
using TildeTools.Modules.EmoteSplitter.Sending;

namespace TildeTools.Modules.EmoteSplitter;

internal sealed class PostingWindow : Window
{
    private const int SnippetLength = 60;

    private readonly SendQueue _queue;
    private readonly ReplyPin _pin;
    private readonly Action _stop;

    // NoFocusOnAppearing: it mustn't take the chat box from someone mid-sentence
    internal PostingWindow(SendQueue queue, ReplyPin pin, Action stop)
        : base("Emote Splitter##posting",
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav)
    {
        (_queue, _pin, _stop) = (queue, pin, stop);

        (ShowCloseButton, RespectCloseHotkey, DisableWindowSounds) = (false, false, true);
    }

    public override void PreOpenCheck() => IsOpen = _queue.State != SendQueueState.Idle;

    // Window.Position has no pivot
    public override void PreDraw()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + new Vector2(viewport.Size.X / 2, viewport.Size.Y / 6), ImGuiCond.FirstUseEver, new Vector2(0.5f, 0));
    }

    public override void Draw()
    {
        var state = _queue.State;
        var asking = state == SendQueueState.Asking;
        ChannelCommands.TrySplittable(asking ? _queue.Suspect! : _queue.Head!, out var header, out var body);

        if (asking)
        {
            DrawQuestion(Where(header), body);
            ImGui.SameLine();
        }
        else if (_queue.HeadPart is (var part, var of))
            ImGui.TextUnformatted($"{(state == SendQueueState.Held ? "Waiting to post" : "Posting")} part {part} of {of} to {Where(header)}");

        if (ImGui.Button("Stop"))
            _stop();
    }

    private void DrawQuestion(string where, string body)
    {
        ImGui.TextUnformatted("The game says a message wasn't heard. It's nearly always one you typed.");
        ImGui.TextUnformatted($"Is this part to {where} in your chat log?");
        ImGui.TextDisabled(body.Length > SnippetLength ? body[..SnippetLength] + "..." : body);

        if (ImGui.Button("Not there, post it again"))
            _queue.Resume(resend: true);

        ImGui.SameLine();

        if (ImGui.Button("It's there, carry on"))
            _queue.Resume(resend: false);
    }

    private string Where(string header)
    {
        if (header.Length == 0)
            return "the chat box's channel";

        // Only a tell's header has a space, before Name@World
        var space = header.IndexOf(' ');
        if (space > 0)
            return header[(space + 1)..];

        return ReplyPin.IsReplyHeader(header)
            ? _pin.Target ?? "whoever last sent you a tell"
            : ChannelCommands.NameOf(header) ?? header;
    }
}
