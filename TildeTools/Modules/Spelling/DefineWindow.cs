using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using TildeTools.Ui;

namespace TildeTools.Modules.Spelling;

internal sealed partial class DefineWindow : Window
{
    // Wiktionary's pointers: "Pronunciation spelling of", "(slang) Alternative form of", "Misspelling of", not "A form of". 'Wuk' as in 'Wuk Lamat' should NOT expose people to dialectical definitions of vaguely racist proportions.
    [GeneratedRegex(@"^(?:\(\w+\) )?(?:(?!(?:A|An|The) )(?:\w+ )*(?:spelling|form) of |Misspelling of )", RegexOptions.IgnoreCase)]
    private static partial Regex SpellingOf();

    // A game name's note, and the word the wikis search for.
    private readonly record struct Named(NameKind Kind, string Note, string Query);

    // Closed: where each entry's senses behind the closed line start, sorted last by Look
    // Bars: those lines' labels, the synonyms' last
    // Shut: the closed synonyms, bare and together
    // Missing: the line saying there's no definition
    private readonly record struct Answer(List<Entry> Entries, int[] Closed, string[] Bars, string Missing, string[] Synonyms, string Shut, Named? Name, string Credit);

    private readonly SpellingSettings _settings;
    private readonly string _lexicon;

    private string _word = "";
    private Task<Answer>? _lookup;

    // Builds once. A failed task's Exception is a new AggregateException each read.
    private string? _failed;
    private readonly Stack<string> _back = new();
    private bool _toTop;

    private string _search = "";
    private bool _focusSearch;

    private string _spelling = "";
    private string _replacement = "";

    private Task? _picked;

    private readonly Dictionary<(string Word, bool Online, bool NamesRead), Task<Answer>> _answers = [];
    private const int MostAnswers = 64;

    // Compared by reference, the keys are the Answer's own strings and stay the same each frame.
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

    // "word: original", or one of its synonyms from the menu: "word (slang)"
    internal void Open(string word, string original, Action<string>? use)
    {
        IsOpen = true;
        _target = use is null ? null : (original, use);
        _back.Clear();

        // Already open, it can sit behind the chat window that asked for it (example, if the user moves the window).
        // I'm actually not super satisfied with this and might force it to always be at the front in a later release. That, or close it when the window is moved.
        BringToFront();
        word = Lexicon.Bare(word);

        if (word != original)
            _back.Push(original);

        Show(word);
    }

    internal void OpenSearch()
    {
        (IsOpen, _target, _focusSearch) = (true, null, true);
        BringToFront();

        if (_word.Length == 0)
            WindowName = "Look up" + Id;
    }

    // A hosted plugin's callback, dropped with the window.
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
        _failed = null;
        _words.Clear();
        Spell(word);
        WindowName = word + Id;

        // Looked up before the game's names were read, a name kept Wiktionary's spelling pointers.
        var key = (word, _settings.LookUpOnline, GameVocabulary.Ready);
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

        // Look finds "sword's" under "sword", so the possessive goes back on.
        if (Lexicon.Stem(original) != original && !_replacement.EndsWith(original[^2..], StringComparison.Ordinal))
            _replacement += original[^2..];
    }

    private async Task<Answer> Look(string word, bool lookUpOnline)
    {
        (List<Entry>, string[][]) Offline(string w) => (Lexicon.Define(_lexicon, w), Lexicon.Synonyms(_lexicon, w));

        var stem = Lexicon.Stem(word);
        var (entries, synonyms) = await Task.Run(() => Offline(word));

        if (entries.Count == 0 && stem != word)
            (entries, synonyms) = await Task.Run(() => Offline(stem));

        // For a FFXIV name the spelling dictionaries lack, a pointer to another word's spelling isn't a meaning. Again, "Wuk" isn't dialect for "work", BUT Bahamut's real world mythic inspirations is good context to have on hand for thematic analysis so it stays.
        var name = GameVocabulary.NameOf(word);
        if (name is not null && !Speller.InDictionary(stem))
            entries = [.. entries.Select(e => e with { Senses = [.. e.Senses.Where(s => !SpellingOf().IsMatch(s.Gloss))] }).Where(e => e.Senses.Count > 0)];

        // A word the dictionaries know needs more than two sightings to be a known name. "Ellipsis" is one group pose stamp, for example.
        if (name is { Kind: NameKind.Other, Seen: <= 2 } && Speller.InDictionary(stem))
            name = null;

        // Wiktionary has no page for a possessive, nor for most of the game's names, so we don't bother.
        var leftOut = entries.Count == 0 && Lexicon.LeftOut(_lexicon, stem);
        var online = entries.Count == 0 && lookUpOnline && name is null && !leftOut;
        if (online)
            entries = await Wiktionary.Define(stem);

        // Synonyms are WordNet's and Wiktionary's
        var credit = synonyms.Length > 0 || entries.Any(e => e.Senses.Any(s => !s.Own))
            ? $"From Wiktionary{(online ? " online" : "")}, CC BY-SA 4.0{(synonyms.Length > 0 ? ", and Open English WordNet, CC BY 4.0" : "")}."
            : "";

        // Slurs, offensive and vulgar ones last, behind a closed little section of their own.
        // Closed synonyms go bare: a slur sense's list has respectful words too, ala "person of color", and the line is clear enough of what you're getting into if you open it.
        // Really torn on handling them at all, they might end up in exclusion hell eventually.
        entries = [.. entries.Select(e => e with { Senses = [.. e.Senses.OrderBy(s => Lexicon.Closed(s.Gloss))] })];
        int[] closed = [.. entries.Select(e => e.Senses.FindIndex(s => Lexicon.Closed(s.Gloss)) is >= 0 and var at ? at : e.Senses.Count)];
        string[] shut = [.. synonyms.SelectMany(group => group).Where(Lexicon.Closed).Select(Lexicon.Bare).Distinct(StringComparer.OrdinalIgnoreCase)];
        string[] open = [.. synonyms.Select(group => string.Join(", ", group.Where(synonym => !Lexicon.Closed(synonym)))).Where(group => group.Length > 0)];

        // An id with the word, so each word's lines start closed however the last ones were left.
        string[] bars = [.. entries.Select((e, i) => $"Potentially NSFW ({e.Senses.Count - closed[i]})###senses-{word}"), $"Potentially NSFW ({shut.Length})###synonyms-{word}"];

        var missing = leftOut ? "Excluded; Politics" : $"No definition for \"{word}\"{(online ? ", here or on Wiktionary." : ". Looking online is off on the Spelling tab.")}";

        return new Answer(entries, closed, bars, missing, open, string.Join(", ", shut), name is { } known ? Describe(known, stem) : null, credit);
    }

    // Search's "go" opens the page when there is one, else the matches; a part several names share searches itself, ala, yet again, 'Wuk'.
    private static Named Describe(GameName name, string stem)
    {
        var whole = name.Full.Equals(stem, StringComparison.OrdinalIgnoreCase);
        var note = name.Kind switch
        {
            NameKind.Creator => $"A possible {name.Full} from the name generator.",
            NameKind.Npc when whole => "FFXIV NPC name.",
            NameKind.Place when whole => "FFXIV place name.",
            NameKind.Other => "A known FFXIV name.",
            _ when name.Others > 0 => $"Part of FFXIV names like {name.Full}, and {name.Others} other{(name.Others == 1 ? "" : "s")}.",
            _ => $"Part of a known FFXIV name: {name.Full}.",
        };

        return new Named(name.Kind, note, name.Others > 0 && !whole ? stem : name.Full);
    }

    // Window.Position has no pivot. CURSE OF IMGUI BE UPON ME
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

    // True once Use has closed the window.
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

        // An HttpClient timeout ends up Canceled, with no Exception.
        if (!lookup.IsCompletedSuccessfully)
        {
            ImGui.TextDisabled(_failed ??= $"Couldn't look it up: {lookup.Exception?.InnerException?.Message ?? "Wiktionary didn't answer in time."}");
            return;
        }

        var (entries, closed, bars, missing, synonyms, shut, name, credit) = lookup.Result;

        if (name is { } known)
            DrawName(known);

        if (entries.Count == 0)
        {
            if (name is null)
                ImGui.TextDisabled(missing);
        }
        else if (entries.Count == 1)
            DrawEntry(entries[0], closed[0], bars[0]);
        else
            DrawSpellings(lookup, entries, closed, bars);

        if (synonyms.Length > 0 || shut.Length > 0)
        {
            ImGui.Spacing();
            if (entries.Count > 0)
                ImGui.Separator();

            ImGui.TextUnformatted("Synonyms");

            foreach (var group in synonyms)
                Linked(group, afterLast: false);

            if (shut.Length > 0)
            {
                using var node = ImRaii.TreeNode(bars[^1], ImGuiTreeNodeFlags.Framed);
                if (node.Success)
                    Linked(shut, afterLast: false);
            }
        }

        if (credit.Length == 0)
            return;

        ImGui.Spacing();
        ImGui.TextDisabled(credit);
    }

    private static void DrawName(Named name)
    {
        ImGui.TextDisabled(name.Note);

        // The name generator's names link the naming guide by Fernehalwas since no wikis seem to include those.
        if (name.Kind == NameKind.Creator)
        {
            if (ImGui.Button("Naming conventions"))
                Widgets.Open("https://forum.square-enix.com/ffxiv/threads/63112-Race-Naming-Conventions");

            ImGui.Spacing();
            return;
        }

        if (ImGui.Button("Console Games Wiki"))
            Widgets.Open($"https://ffxiv.consolegameswiki.com/mediawiki/index.php?search={Uri.EscapeDataString(name.Query)}&title=Special%3ASearch&go=Go");

        ImGui.SameLine();
        if (ImGui.Button("Gamer Escape"))
            Widgets.Open($"https://ffxiv.gamerescape.com/w/index.php?search={Uri.EscapeDataString(name.Query)}&title=Special%3ASearch&go=Go");

        ImGui.Spacing();
    }

    private void DrawSpellings(Task lookup, List<Entry> entries, int[] closed, string[] bars)
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
                DrawEntry(entries[i], closed[i], bars[i]);
        }
    }

    private void DrawEntry(Entry entry, int closed, string bar)
    {
        if (entry.Word != _spelling)
            Spell(entry.Word);

        var heading = entry.Word;

        void DrawSenses(int from, int to)
        {
            for (var i = from; i < to; i++)
            {
                var (word, pos, gloss, _) = entry.Senses[i];
                if (word != heading)
                {
                    ImGui.Spacing();
                    ImGui.TextUnformatted(heading = word);
                }

                ImGui.TextDisabled(pos);
                Linked(gloss, afterLast: true);
            }
        }

        DrawSenses(0, closed);
        if (closed == entry.Senses.Count)
            return;

        using var node = ImRaii.TreeNode(bar, ImGuiTreeNodeFlags.Framed);
        if (node.Success)
            DrawSenses(closed, entry.Senses.Count);
    }

    // Wrapped by hand because ImGui can't say which word of wrapped text was clicked...
    // the discord's going to kill me with a brick i can feel it
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

            // Finds the letter under the pointer, then steps past any dash or slash WordAt gives null at, like "and/or" or "—what"
            var (min, max) = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
            var letter = 0;
            while (letter < token.Length - 1 && ImGui.CalcTextSize(token.AsSpan(0, letter + 1)).X < ImGui.GetMousePos().X - min.X)
                letter++;

            while (letter < token.Length && !char.IsLetterOrDigit(token[letter]))
                letter++;

            // A hyphen joins a word for spelling, but a link follows the half under the pointer, like "river-wood", so 'river' and 'wood' are searchable fragments.
            if (SpellCheck.WordAt(token, letter) is not var (word, size) || SpellCheck.HalfAt(token, word, size, letter) is not var (start, length))
                continue;

            var left = min.X + ImGui.CalcTextSize(token.AsSpan(0, start)).X;
            ImGui.GetWindowDrawList().AddLine(new Vector2(left, max.Y), new Vector2(left + ImGui.CalcTextSize(token.AsSpan(start, length)).X, max.Y), ImGui.GetColorU32(ImGuiCol.Text));
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            if (ImGui.IsItemClicked())
                Follow(token.Substring(start, length));
        }
    }
}
