using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Ipc;
using TildeTools.Modules.EmoteSplitter.Chat;
using Stamp = (int Generation, bool Hyphen, int Suggestions);

namespace TildeTools.Modules.Spelling;

// Asked by C2, XIM and WS over IPC, and called directly for the default chatbox.
internal sealed class SpellIpc : IDisposable
{
    private const int ApiVersion = 6;
    private const string Prefix = "TildeTools.Spell.";

    private readonly SpellingSettings _settings;
    private readonly Action _save;
    private readonly string _lexicon;
    private readonly Action<string, string, Action<string>?> _openDefine;

    private readonly ICallGateProvider<int> _apiVersion = Svc.Pi.GetIpcProvider<int>($"{Prefix}ApiVersion");
    private readonly ICallGateProvider<string, List<int>> _check = Svc.Pi.GetIpcProvider<string, List<int>>($"{Prefix}Check");
    private readonly ICallGateProvider<string, int, List<string>> _suggestNow = Svc.Pi.GetIpcProvider<string, int, List<string>>($"{Prefix}SuggestNow");
    private readonly ICallGateProvider<string, bool> _isWord = Svc.Pi.GetIpcProvider<string, bool>($"{Prefix}IsWord");
    private readonly ICallGateProvider<string, bool> _addToDictionary = Svc.Pi.GetIpcProvider<string, bool>($"{Prefix}AddToDictionary");
    private readonly ICallGateProvider<string, int, List<int>> _wordAt = Svc.Pi.GetIpcProvider<string, int, List<int>>($"{Prefix}WordAt");
    private readonly ICallGateProvider<string, bool> _define = Svc.Pi.GetIpcProvider<string, bool>($"{Prefix}Define");
    private readonly ICallGateProvider<bool> _lookup = Svc.Pi.GetIpcProvider<bool>($"{Prefix}Lookup");
    private readonly ICallGateProvider<string, string, bool, Action<string>, bool> _drawMenu = Svc.Pi.GetIpcProvider<string, string, bool, Action<string>, bool>($"{Prefix}DrawMenu");
    private readonly ICallGateProvider<object?> _available = Svc.Pi.GetIpcProvider<object?>($"{Prefix}Available");

    internal SpellMenu Menu { get; }

    internal SpellIpc(SpellingSettings settings, Action save, string lexicon, Action<string, string, Action<string>?> define, Action lookup)
    {
        _settings = settings;
        _save = save;
        _lexicon = lexicon;
        _openDefine = define;
        Menu = new SpellMenu(this);

        _apiVersion.RegisterFunc(() => ApiVersion);
        _check.RegisterFunc(Check);
        // WS asks with 0, A.K.A. the Spelling tab's count.
        _suggestNow.RegisterFunc((word, most) => Lookup(word, most > 0 ? most : _settings.MaximumSuggestions));
        _isWord.RegisterFunc(word => !Speller.Loaded || Speller.IsWord(word));
        _addToDictionary.RegisterFunc(AddToDictionary);
        _wordAt.RegisterFunc((text, index) => WordAt(text, index) is var (start, length) ? [start, length] : []);
        _define.RegisterFunc(word => Define(word, word, null));
        _lookup.RegisterFunc(() =>
        {
            lookup();
            return true;
        });

        // Drawn into the caller's popup. Every plugin share draws into Dalamud's ImGui context, so, IPC.
        _drawMenu.RegisterFunc((id, word, misspelled, replace) =>
        {
            SpellMenu.KeepOnScreen();
            return Menu.Draw(id, word, misspelled, replace);
        });

        _available.SendMessage();
        Svc.Pi.UiBuilder.Draw += Announce;
    }

    private Stamp _announced;

    // The boxes drop their cached marks on Available.
    // Sent from gamethread Draw where they read those caches.
    private void Announce()
    {
        if (Current == _announced)
            return;

        _announced = Current;
        _available.SendMessage();
    }

    private static int UnfinishedWordAt(string text)
    {
        if (text is not [.., var last] || char.IsWhiteSpace(last) || char.IsPunctuation(last) && last is not ('\'' or '-'))
            return text.Length;

        var start = text.Length;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
            start--;

        return start;
    }

    private static int CommandEndsAt(string text)
    {
        if (text.Length == 0 || text[0] != '/')
            return 0;

        var end = text.IndexOf(' ');
        if (end < 0)
            return text.Length;

        // A tell's target is First Last@World or a placeholder like <t>
        if (ChannelCommands.IsTell(text))
        {
            var first = text.IndexOf(' ', end + 1);
            if (first < 0)
                return text.Length;

            if (text[end + 1] == '<')
                return first;

            var second = text.IndexOf(' ', first + 1);
            return second < 0 ? text.Length : second;
        }

        return end;
    }

    // Flat start/length pairs, because only framework types can cross between plugins.
    private List<int> Check(string text)
    {
        var marks = Marks(text);

        List<int> flat = new(marks.Count * 2);
        foreach (var (index, length) in marks)
        {
            flat.Add(index);
            flat.Add(length);
        }

        return flat;
    }

    // A cut between words changes nothing since SpellCheck checks each word alone.
    // Typing at the end rechecks only the last segment for performance reasons.
    internal List<(int Index, int Length)> Marks(string text)
    {
        List<(int Index, int Length)> marks = [];

        try
        {
            if (!Speller.Loaded)
                return marks;

            var (from, to) = (CommandEndsAt(text), UnfinishedWordAt(text));

            DropStale(_segments, ref _segmentsStamp, MostSegments);
            var cached = _segments.GetAlternateLookup<ReadOnlySpan<char>>();

            for (int start = 0, end; start < text.Length; start = end)
            {
                end = SegmentEnd(text, start);

                if (!cached.TryGetValue(text.AsSpan(start, end - start), out var found))
                {
                    var segment = text[start..end];
                    _segments[segment] = found = SpellCheck.Misspellings(segment, _settings.IgnoreWordsEndingInHyphen);
                }

                foreach (var (index, length) in found)
                    if (start + index >= from && start + index < to)
                        marks.Add((start + index, length));
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Spellcheck failed.");
        }

        return marks;
    }

    internal Stamp Current => (Speller.Generation, _settings.IgnoreWordsEndingInHyphen, _settings.MaximumSuggestions);

    private void DropStale<T>(Dictionary<string, T> cache, ref Stamp stamp, int most)
    {
        if (stamp == Current && cache.Count <= most)
            return;

        cache.Clear();
        stamp = Current;
    }

    private readonly Dictionary<string, List<(int Index, int Length)>> _segments = [];
    private Stamp _segmentsStamp;

    private const int SegmentLength = 256;

    // MaxUnlockBytes' 32000 is ~125 segments and its split parts are another ~125, so 4096 holds ~16 of both.
    // DropStale empties the lot when full.
    private const int MostSegments = 4096;

    private static int SegmentEnd(string text, int start)
    {
        var end = Math.Min(text.Length, start + SegmentLength);

        while (end < text.Length && !char.IsWhiteSpace(text[end]))
            end++;

        while (end < text.Length && char.IsWhiteSpace(text[end]))
            end++;

        return end;
    }

    // A hyphenated word the lexicon doesn't define is two words to Synonyms and Define, like "river-wood".
    internal (int Index, int Length)? WordAt(string text, int index)
    {
        var word = SpellCheck.WordAt(text, index);
        if (word is not var (start, length) || text.IndexOf('-', start, length) < 0 || Lexicon.Define(_lexicon, text.Substring(start, length)).Count > 0)
            return word;

        return SpellCheck.HalfAt(text, start, length, index);
    }

    private readonly Dictionary<string, Task<List<string>>> _suggesting = [];
    private Stamp _suggestingStamp;

    private const int MostSuggesting = 64;

    // The menus ask again each frame that it's null.
    internal List<string>? Suggest(string word)
    {
        if (!Speller.Loaded || string.IsNullOrWhiteSpace(word))
            return [];

        DropStale(_suggesting, ref _suggestingStamp, MostSuggesting);

        if (!_suggesting.TryGetValue(word, out var lookup))
            _suggesting[word] = lookup = Task.Run(() => Lookup(word, _settings.MaximumSuggestions));

        return lookup.IsCompleted ? lookup.Result : null;
    }

    // Logged here because the plugins asking swallow a gate's throw.
    private static List<string> Lookup(string word, int most)
    {
        try
        {
            return Speller.Suggest(word, most);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Spelling suggestions failed.");
            return [];
        }
    }

    // Returns true even when the save throws, because the word is in and the next save keeps it.
    internal bool AddToDictionary(string word)
    {
        if (!Speller.AddWord(word))
            return false;

        _settings.CustomWords.Add(word.Trim());

        try
        {
            _save();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Saving the added word failed.");
        }

        return true;
    }

    // A Func, not an action, as every IPC caller uses InvokeFunc
    internal bool Define(string word, string original, Action<string>? use)
    {
        _openDefine(word, original, use);
        return true;
    }

    private const int MostSynonyms = 10;

    // DefineWindow.Spell matches the case
    // A lowercase word's capitalized synonyms name something else (Bush's nicknames, not bush's)
    internal List<string> Synonyms(string word) =>
    [
        .. Lexicon.Synonyms(_lexicon, word)
            .SelectMany(group => group)
            .Where(synonym => !Lexicon.Closed(synonym) && !(word is [var first, ..] && char.IsLower(first) && synonym is [var cap, var next, ..] && char.IsUpper(cap) && char.IsLower(next)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MostSynonyms),
    ];

    public void Dispose()
    {
        Svc.Pi.UiBuilder.Draw -= Announce;
        foreach (var gate in new ICallGateProvider[] { _apiVersion, _check, _suggestNow, _isWord, _addToDictionary, _wordAt, _define, _lookup, _drawMenu })
            gate.UnregisterFunc();

        // After unregistering, so the boxes find the gates gone and drop their marks.
        _available.SendMessage();
    }
}
