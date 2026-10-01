using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace TildeTools.Modules.Spelling;

// The spelling menu's entries, drawn into whichever menu hosts them, be that the game's chat box or C2's and XIM's windows over IPC.
// Kept by menu id and word, so a menu's synonyms and corrections are asked for once.
internal sealed class SpellMenu(SpellIpc ipc)
{
    private sealed class Shown(string word)
    {
        internal readonly string Word = word;
        internal List<string>? Synonyms, Corrections;
    }

    // One per menu opened, emptied at 8.
    private readonly Dictionary<string, Shown> _shown = [];

    // True once it's done with the word and its been defined, added, ignored or replaced.
    internal bool Draw(string id, string word, bool misspelled, Action<string> replace)
    {
        // Each XIM window and C2 pop-out counts its menus from 1. IDs all repeat.
        if (!_shown.TryGetValue(id, out var shown) || shown.Word != word)
        {
            if (_shown.Count >= 8)
                _shown.Clear();

            _shown[id] = shown = new(word);
        }

        ImGui.TextDisabled(word);

        // ##entry keeps an entry's id apart from a correction's, so you can correct 'defin' to 'Define' without tainting.
        if (ImGui.Selectable("Synonyms##entry", false, ImGuiSelectableFlags.DontClosePopups))
            shown.Synonyms = shown.Synonyms is null ? ipc.Synonyms(word) : null;

        if (shown.Synonyms is not null)
        {
            // A synonym can share a label with a correction.
            using var synonyms = ImRaii.PushId("synonyms");
            using var indent = ImRaii.PushIndent();

            if (shown.Synonyms.Count == 0)
                ImGui.TextDisabled("None found");

            foreach (var synonym in shown.Synonyms)
                if (ImGui.Selectable(synonym))
                {
                    ipc.Define(synonym, word, replace);
                    return true;
                }
        }

        if (ImGui.Selectable("Define##entry"))
        {
            ipc.Define(word, word, replace);
            return true;
        }

        if (!misspelled)
            return false;

        ImGui.Separator();

        if (ImGui.Selectable("Add to dictionary##entry"))
        {
            ipc.AddToDictionary(word);
            return true;
        }

        if (ImGui.Selectable("Ignore for now##entry"))
        {
            Speller.Ignore(word);
            return true;
        }

        var corrections = shown.Corrections ??= ipc.Suggest(word);
        if (corrections is { Count: 0 })
            return false;

        ImGui.Separator();

        if (corrections is null)
        {
            ImGui.TextDisabled("Looking for corrections...");
            return false;
        }

        foreach (var correction in corrections)
            if (ImGui.Selectable(correction))
            {
                replace(correction);
                return true;
            }

        return false;
    }

    // ImGui only keeps its popups on screen as it opens, and corrections arrive later and make the menu taller, so this pushes it back inside the screen every frame.
    internal static void KeepOnScreen()
    {
        var screen = ImGui.GetWindowViewport();
        var at = ImGui.GetWindowPos();
        var fit = Vector2.Clamp(at, screen.WorkPos, Vector2.Max(screen.WorkPos, screen.WorkPos + screen.WorkSize - ImGui.GetWindowSize()));

        if (fit != at)
            ImGui.SetWindowPos(fit);
    }
}
