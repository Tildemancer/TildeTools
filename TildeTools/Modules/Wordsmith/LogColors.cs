using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.Config;
using TildeTools.Modules.EmoteSplitter.Chat;

namespace TildeTools.Modules.Wordsmith;

// The Log Text Colors for a WS pad header's channel.
internal static class LogColors
{
    private static readonly UiConfigOption[] Linkshells =
    [
        UiConfigOption.ColorLS1, UiConfigOption.ColorLS2, UiConfigOption.ColorLS3, UiConfigOption.ColorLS4,
        UiConfigOption.ColorLS5, UiConfigOption.ColorLS6, UiConfigOption.ColorLS7, UiConfigOption.ColorLS8,
    ];

    private static readonly UiConfigOption[] CrossWorld =
    [
        UiConfigOption.ColorCWLS, UiConfigOption.ColorCWLS2, UiConfigOption.ColorCWLS3, UiConfigOption.ColorCWLS4,
        UiConfigOption.ColorCWLS5, UiConfigOption.ColorCWLS6, UiConfigOption.ColorCWLS7, UiConfigOption.ColorCWLS8,
    ];

    // A one-time find for headers since the pad asks every frame. The alternative is working it out again every frame, which, well...
    private static readonly Dictionary<string, UiConfigOption?> Options = [];

    // Null with no known channel, or a color the game leaves at 0, as C2 reads it.
    // The color is read each time, so a change in the game's settings shows immediately.
    internal static Vector4? Of(string header)
    {
        if (!Options.TryGetValue(header, out var found))
            Options[header] = found = (ChannelCommands.IsTell(header) ? ChannelCommands.Tell : ChannelCommands.NameOf(header)) is { } channel ? Option(channel) : null;

        if (found is not { } option || !Svc.GameConfig.TryGet(option, out uint color) || (color & 0xFFFFFF) == 0)
            return null;

        return new Vector4(((color >> 16) & 0xFF) / 255f, ((color >> 8) & 0xFF) / 255f, (color & 0xFF) / 255f, 1f);
    }

    private static UiConfigOption? Option(string channel) => channel switch
    {
        "Say" => UiConfigOption.ColorSay,
        "Shout" => UiConfigOption.ColorShout,
        "Yell" => UiConfigOption.ColorYell,
        ChannelCommands.Tell or ChannelCommands.Reply => UiConfigOption.ColorTell,
        ChannelCommands.Party => UiConfigOption.ColorParty,
        ChannelCommands.Alliance => UiConfigOption.ColorAlliance,
        "Free Company" => UiConfigOption.ColorFCompany,
        "Novice Network" => UiConfigOption.ColorBeginner,
        "PvP Team" => UiConfigOption.ColorPvPGroup,
        "Emote" => UiConfigOption.ColorEmoteUser,
        "Echo" => UiConfigOption.ColorEcho,
        "Cross-world Linkshell" => UiConfigOption.ColorCWLS,
        _ when Number(channel, "Cross-world Linkshell ") is { } n => CrossWorld[n - 1],
        _ when Number(channel, "Linkshell ") is { } n => Linkshells[n - 1],
        _ => null,
    };

    private static int? Number(string channel, string prefix) =>
        channel.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(channel.AsSpan(prefix.Length), out var n) && n is >= 1 and <= 8 ? n : null;
}
