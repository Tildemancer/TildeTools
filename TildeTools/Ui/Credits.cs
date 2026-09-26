using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace TildeTools.Ui;

// TildeTools is AGPL-3.0 like Messenger, and Chat 2's EUPL-1.2 lists AGPL-3.0 as compatible
// Wordsmith has no licence upstream, left as it is
internal static class Credits
{
    private readonly record struct Work(string Name, string Author, string Summary, string Changes, string Source, (string Label, string Url)[] Funding);

    private static readonly Work[] Bundled =
    [
        new(
            "Chat 2",
            "Anna (lojewalo) and Infi",
            "A replacement for the game's chat window, with tabs you set up.",
            "Long messages are split and previewed as the parts they will be sent as, "
            + "and misspellings are marked in the input and the preview.",
            "https://github.com/Infiziert90/ChatTwo",
            [("Anna's Ko-fi", "https://ko-fi.com/lojewalo"), ("Infi's Ko-fi", "https://ko-fi.com/infiii")]),

        new(
            "XIV Instant Messenger",
            "NightmareXIV",
            "Direct messages in their own windows, after WoW's Instant Messenger.",
            "Over-length tells and channel messages are handed to Emote Splitter whole instead of being "
            + "sent a part per keypress, and misspellings are marked as you type.",
            "https://github.com/NightmareXIV/XIVInstantMessenger",
            [("NightmareXIV's Patreon", "https://www.patreon.com/NightmareXIV")]),

        new(
            "Wordsmith",
            "Lady Defile",
            "A roleplay scratch pad, thesaurus and spellchecker.",
            "The copy button sends through Emote Splitter rather than filling the clipboard, "
            + "and its spellcheck and thesaurus come from TildeTools' Spelling module, not a downloaded word list and Merriam-Webster.",
            "https://github.com/MythicPalette/Wordsmith-DalamudPlugin",
            []),
    ];

    private static (string Label, string Url)[] WordsmithFunding() =>
        global::Wordsmith.Hosting.KofiUrl is { Length: > 0 } kofi ? [("Lady Defile's Ko-fi", kofi)] : [];

    private static readonly (string Name, string What, string Url)[] Libraries =
    [
        ("WeCantSpell.Hunspell", "Hunspell, in C#. The spellchecker and its suggestions.",
            "https://github.com/aarondandy/WeCantSpell.Hunspell"),
        ("SCOWL", "The American English dictionary, by Kevin Atkinson. Distributed under its own notice, "
            + "kept beside it as COPYRIGHT-SCOWL.txt.", "http://wordlist.sourceforge.net"),
        ("British English dictionary", "By David Bartlett, Andrew Brown and Marco A.G. Pinto, under the LGPL 2.1. "
            + "Trimmed here of the words the American one already has; its notice is in en_GB.aff, and the "
            + "licence beside it as COPYING-LGPL-2.1.txt.",
            "https://proofingtoolgui.org"),
        ("Wiktionary", "The definitions, and synonyms WordNet lacks, by its contributors through kaikki.org's "
            + "extract, under CC BY-SA 4.0. Notice in Lexicon/COPYING-Wiktionary.txt.", "https://en.wiktionary.org"),
        ("Open English WordNet", "The synonyms, by the Open English Wordnet team from Princeton WordNet, under "
            + "CC BY 4.0. Notice in Lexicon/COPYING-WordNet.txt.", "https://github.com/globalwordnet/english-wordnet"),
        ("ECommons", "Shared plumbing that Instant Messenger is built on, by NightmareXIV.",
            "https://github.com/NightmareXIV/ECommons"),
    ];

    private static readonly Vector4 Heading = new(0.85f, 0.78f, 0.55f, 1f);

    internal static void Draw()
    {
        using var wrap = ImRaii.TextWrapPos(0f);

        ImGui.TextWrapped(
            "TildeTools bundles modified copies of the plugins below. Their authors don't "
            + "distribute this build and aren't responsible for it. Their donation links are here.");

        ImGui.Spacing();

        foreach (var work in Bundled)
            DrawWork(work);

        ImGui.Separator();
        ImGui.TextColored(Heading, "Also used");

        foreach (var (name, what, url) in Libraries)
        {
            using var id = ImRaii.PushId(name);
            ImGui.Bullet();
            ImGui.SameLine();

            if (ImGui.SmallButton(name))
                Open(url);

            ImGui.SameLine();
            ImGui.TextWrapped(what);
        }
    }

    private static void DrawWork(Work work)
    {
        using var id = ImRaii.PushId(work.Name);
        ImGui.Separator();

        ImGui.TextColored(Heading, work.Name);
        ImGui.SameLine();
        ImGui.TextDisabled($"by {work.Author}");

        ImGui.TextWrapped(work.Summary);

        ImGui.TextDisabled("Changed here:");
        using (ImRaii.PushIndent())
            ImGui.TextWrapped(work.Changes);

        ImGui.Spacing();

        foreach (var (label, url) in work.Name == "Wordsmith" ? WordsmithFunding() : work.Funding)
        {
            if (ImGui.Button(label))
                Open(url);

            ImGui.SameLine();
        }

        if (ImGui.Button("Source"))
            Open(work.Source);

        ImGui.Spacing();
    }

    private static void Open(string url)
    {
        try
        {
            Dalamud.Utility.Util.OpenLink(url);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"Could not open {url}.");
        }
    }
}
