using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace TildeTools.Modules.Spelling;

internal sealed class DefineWindow : Window
{
    private readonly record struct Answer(List<Entry> Entries, bool Online, string[] Synonyms, string Credit);

    private readonly SpellingSettings _settings;
    private readonly string _lexicon;

    private string _word = "";
    private Task<Answer>? _lookup;
    private readonly Stack<string> _back = new();
    private bool _toTop;

    private string _search = "";
    private bool _focusSearch;

    private string _spelling = "";
    private string _replacement = "";

    private Task? _picked;

    private readonly Dictionary<(string Word, bool Online), Task<Answer>> _answers = [];
    private const int MostAnswers = 64;

    // By reference: keys are the Answer's own strings, the same each frame
    private readonly Dictionary<string, string[]> _words = new(ReferenceEqualityComparer.Instance);

    private const string Id = "###tildetools-define";

    internal DefineWindow(SpellingSettings settings, string lexicon)
        : base(Id)
    {
        (_settings, _lexicon) = (settings, lexicon);

        Size = new Vector2(380, 300);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    private (string Original, Action<string> Use)? _target;

    // word: original, or one of its synonyms from the menu
    internal void Open(string word, string original, Action<string>? use)
    {
        IsOpen = true;
        _target = use is null ? null : (original, use);
        _back.Clear();

        if (word != original)
            _back.Push(original);

        Show(word);
    }

    internal void OpenSearch()
    {
        (IsOpen, _target, _focusSearch) = (true, null, true);

        if (_word.Length == 0)
            WindowName = "Look up" + Id;
    }

    // A hosted plugin's callback, dropped with the window
    public override void OnClose() => _target = null;

    private void Follow(string word)
    {
        if (_word.Length > 0 && _word != word)
            _back.Push(_word);

        Show(word);
    }

    private void Show(string word)
    {
        _toTop = true;
        _word = word;
        _words.Clear();
        Spell(word);
        WindowName = word + Id;

        var key = (word, _settings.LookUpOnline);
        if (_answers.TryGetValue(key, out var answer) && !answer.IsFaulted && !answer.IsCanceled)
        {
            _lookup = answer;
            return;
        }

        if (_answers.Count >= MostAnswers)
            _answers.Clear();

        _lookup = _answers[key] = Look(word, key.LookUpOnline);
    }

    private void Spell(string spelling)
    {
        _spelling = _replacement = spelling;

        if (_target is not var (original, _))
            return;

        if (original.Length > 0 && char.IsUpper(original[0]) && spelling.Length > 0)
            _replacement = char.ToUpperInvariant(spelling[0]) + spelling[1..];

        // Look finds "sword's" under "sword", so the 's goes back on
        if (original is [.., '\'' or '’', 's'] && !_replacement.EndsWith(original[^2..], StringComparison.Ordinal))
            _replacement += original[^2..];
    }

    private async Task<Answer> Look(string word, bool lookUpOnline)
    {
        (List<Entry>, string[]) Offline(string w) => (Lexicon.Define(_lexicon, w), Lexicon.Synonyms(_lexicon, w));

        var stem = word is [_, .., '\'' or '’', 's'] ? word[..^2] : word;
        var (entries, synonyms) = await Task.Run(() => Offline(word));

        if (entries.Count == 0 && stem != word)
            (entries, synonyms) = await Task.Run(() => Offline(stem));

        // Wiktionary has no page for a possessive
        var online = entries.Count == 0 && lookUpOnline;
        if (online)
            entries = await Wiktionary.Define(stem);

        // Synonyms are WordNet's and Wiktionary's
        var credit = synonyms.Length > 0 || entries.Any(e => e.Senses.Any(s => !s.Own))
            ? $"From Wiktionary{(online ? " online" : "")}, CC BY-SA 4.0{(synonyms.Length > 0 ? ", and Open English WordNet, CC BY 4.0" : "")}."
            : "";

        return new Answer(entries, online, synonyms, credit);
    }

    // Window.Position has no pivot
    public override void PreDraw()
    {
        var mouse = ImGui.GetMousePos();
        var screen = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(mouse, ImGuiCond.Appearing, new Vector2(mouse.X > screen.X / 2 ? 1 : 0, mouse.Y > screen.Y / 2 ? 1 : 0));
    }

    public override void Draw()
    {
        if (_toTop)
        {
            ImGui.SetScrollY(0);
            _toTop = false;
        }

        using var wrap = ImRaii.TextWrapPos(0f);

        if (_focusSearch)
            ImGui.SetKeyboardFocusHere();

        _focusSearch = false;
        ImGui.SetNextItemWidth(-1);

        if (ImGui.InputTextWithHint("##lookup", "Look up a word", ref _search, 64, ImGuiInputTextFlags.EnterReturnsTrue) && SpellCheck.Trimmed(_search, 0, _search.Length) is var (at, length))
        {
            Follow(_search.Substring(at, length));
            _search = "";
        }

        if (_word.Length > 0 && !DrawNavigation())
            DrawResult();
    }

    // True once Use has closed the window
    private bool DrawNavigation()
    {
        var back = _back.TryPeek(out var previous);
        if (back && ImGui.Button($"Back to {previous}"))
            Show(_back.Pop());

        if (_target is not var (original, use) || _replacement == original)
            return false;

        if (back)
            ImGui.SameLine();

        if (!ImGui.Button($"Use \"{_replacement}\""))
            return false;

        use(_replacement);
        (_target, IsOpen) = (null, false);
        return true;
    }

    private void DrawResult()
    {
        if (_lookup is not { IsCompleted: true } lookup)
        {
            ImGui.TextDisabled("Looking it up...");
            return;
        }

        // An HttpClient timeout ends it Canceled, with no Exception
        if (!lookup.IsCompletedSuccessfully)
        {
            ImGui.TextDisabled($"Couldn't look it up: {lookup.Exception?.InnerException?.Message ?? "Wiktionary didn't answer in time."}");
            return;
        }

        var (entries, online, synonyms, credit) = lookup.Result;

        if (entries.Count == 0)
            ImGui.TextDisabled($"No definition for \"{_word}\"{(online ? ", here or on Wiktionary." : ". Looking online is off on the Spelling tab.")}");
        else if (entries.Count == 1)
            DrawEntry(entries[0]);
        else
            DrawSpellings(lookup, entries);

        if (synonyms.Length > 0)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("Synonyms");

            foreach (var group in synonyms)
                Linked(group, afterLast: false);
        }

        if (credit.Length == 0)
            return;

        ImGui.Spacing();
        ImGui.TextDisabled(credit);
    }

    private void DrawSpellings(Task lookup, List<Entry> entries)
    {
        var first = -1;
        if (_picked != lookup)
        {
            _picked = lookup;
            first = entries.FindIndex(e => e.Word == _word);

            if (first < 0)
                first = Math.Max(0, entries.FindIndex(e => e.Word.Equals(_word, StringComparison.OrdinalIgnoreCase)));
        }

        using var tabs = ImRaii.TabBar("##spellings");
        if (!tabs.Success)
            return;

        for (var i = 0; i < entries.Count; i++)
        {
            using var tab = ImRaii.TabItem(entries[i].Word, i == first ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None);
            if (tab.Success)
                DrawEntry(entries[i]);
        }
    }

    private void DrawEntry(Entry entry)
    {
        if (entry.Word != _spelling)
            Spell(entry.Word);

        var heading = entry.Word;

        foreach (var (word, pos, gloss, _) in entry.Senses)
        {
            if (word != heading)
            {
                ImGui.Spacing();
                ImGui.TextUnformatted(heading = word);
            }

            ImGui.TextDisabled(pos);
            Linked(gloss, afterLast: true);
        }
    }

    // Wrapped by hand: ImGui can't say which word of wrapped text was clicked
    private void Linked(string text, bool afterLast)
    {
        var (space, right) = (ImGui.CalcTextSize(" ").X, ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X);

        if (!_words.TryGetValue(text, out var words))
            _words[text] = words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in words)
        {
            if (afterLast && ImGui.GetItemRectMax().X + space + ImGui.CalcTextSize(token).X <= right)
                ImGui.SameLine(0, space);

            afterLast = true;
            ImGui.TextUnformatted(token);

            if (!ImGui.IsItemHovered())
                continue;

            // The letter under the pointer, then past any dash or slash WordAt gives null at: "and/or", "—what"
            var (min, max) = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
            var letter = 0;
            while (letter < token.Length - 1 && ImGui.CalcTextSize(token[..(letter + 1)]).X < ImGui.GetMousePos().X - min.X)
                letter++;

            while (letter < token.Length && !char.IsLetterOrDigit(token[letter]))
                letter++;

            // A hyphen joins a word for spelling, but a link follows the half under the pointer: "river-wood"
            if (SpellCheck.WordAt(token, letter) is not var (word, size) || SpellCheck.HalfAt(token, word, size, letter) is not var (start, length))
                continue;

            var left = min.X + ImGui.CalcTextSize(token[..start]).X;
            ImGui.GetWindowDrawList().AddLine(new Vector2(left, max.Y), new Vector2(left + ImGui.CalcTextSize(token.Substring(start, length)).X, max.Y), ImGui.GetColorU32(ImGuiCol.Text));
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            if (ImGui.IsItemClicked())
                Follow(token.Substring(start, length));
        }
    }
}
