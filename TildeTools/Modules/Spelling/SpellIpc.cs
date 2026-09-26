using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Ipc;
using TildeTools.Modules.EmoteSplitter.Chat;
using Stamp = (int Generation, bool Hyphen, int Suggestions);

namespace TildeTools.Modules.Spelling;

// Asked by Chat 2, Messenger and Wordsmith over IPC, and called directly for the game's chat box
internal sealed class SpellIpc : IDisposable
{
    private const int ApiVersion = 5;
    private const string Prefix = "TildeTools.Spell.";

    private readonly SpellingSettings _settings;
    private readonly Action _save;
    private readonly string _lexicon;
    private readonly Action<string, string, Action<string>?> _openDefine;

    private readonly ICallGateProvider<int> _apiVersion = Svc.Pi.GetIpcProvider<int>($"{Prefix}ApiVersion");
    private readonly ICallGateProvider<string, List<int>> _check = Svc.Pi.GetIpcProvider<string, List<int>>($"{Prefix}Check");
    private readonly ICallGateProvider<string, List<string>?> _suggest = Svc.Pi.GetIpcProvider<string, List<string>?>($"{Prefix}Suggest");
    private readonly ICallGateProvider<string, int, List<string>> _suggestNow = Svc.Pi.GetIpcProvider<string, int, List<string>>($"{Prefix}SuggestNow");
    private readonly ICallGateProvider<string, bool> _isWord = Svc.Pi.GetIpcProvider<string, bool>($"{Prefix}IsWord");
    private readonly ICallGateProvider<string, bool> _addToDictionary = Svc.Pi.GetIpcProvider<string, bool>($"{Prefix}AddToDictionary");
    private readonly ICallGateProvider<string, bool> _ignore = Svc.Pi.GetIpcProvider<string, bool>($"{Prefix}Ignore");
    private readonly ICallGateProvider<string, int, List<int>> _wordAt = Svc.Pi.GetIpcProvider<string, int, List<int>>($"{Prefix}WordAt");
    private readonly ICallGateProvider<string, List<string>> _synonyms = Svc.Pi.GetIpcProvider<string, List<string>>($"{Prefix}Synonyms");
    private readonly ICallGateProvider<string, string, Action<string>?, bool> _define = Svc.Pi.GetIpcProvider<string, string, Action<string>?, bool>($"{Prefix}Define");
    private readonly ICallGateProvider<bool> _lookup = Svc.Pi.GetIpcProvider<bool>($"{Prefix}Lookup");
    private readonly ICallGateProvider<object?> _available = Svc.Pi.GetIpcProvider<object?>($"{Prefix}Available");

    internal SpellIpc(SpellingSettings settings, Action save, string lexicon, Action<string, string, Action<string>?> define, Action lookup)
    {
        _settings = settings;
        _save = save;
        _lexicon = lexicon;
        _openDefine = define;

        _apiVersion.RegisterFunc(() => ApiVersion);
        _check.RegisterFunc(Check);
        _suggest.RegisterFunc(Suggest);
        // Wordsmith asks with 0: the Spelling tab's count
        _suggestNow.RegisterFunc((word, most) => Lookup(word, most > 0 ? most : _settings.MaximumSuggestions));
        _isWord.RegisterFunc(word => !Speller.Loaded || Speller.IsWord(word));
        _addToDictionary.RegisterFunc(AddToDictionary);
        _ignore.RegisterFunc(Ignore);
        _wordAt.RegisterFunc((text, index) => WordAt(text, index) is var (start, length) ? [start, length] : []);
        _synonyms.RegisterFunc(Synonyms);
        _define.RegisterFunc(Define);
        _lookup.RegisterFunc(() =>
        {
            lookup();
            return true;
        });

        _available.SendMessage();
        Svc.Pi.UiBuilder.Draw += Announce;
    }

    private Stamp _announced;

    // The boxes drop their cached marks on Available
    // Sent from Draw, the game's thread, where they read those caches
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

    // Flat start/length pairs: only framework types cross between plugins
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

    // SpellCheck checks each word alone, so a cut between words changes nothing
    // Typing at the end rechecks only the last segment
    // Unsegmented: 6 ms a keystroke at 16000 characters
    internal List<(int Index, int Length)> Marks(string text)
    {
        List<(int Index, int Length)> marks = [];

        try
        {
            if (!Speller.Loaded || string.IsNullOrEmpty(text))
                return marks;

            var (from, to) = (CommandEndsAt(text), UnfinishedWordAt(text));

            lock (_segments)
            {
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
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Spellcheck failed.");
        }

        return marks;
    }

    private Stamp Current => (Speller.Generation, _settings.IgnoreWordsEndingInHyphen, _settings.MaximumSuggestions);

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

    // MaxUnlockBytes' 32000 is ~125 segments, its split parts as many again: 4096 holds ~16 of both
    // DropStale empties the lot when full
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

    // A hyphenated word the lexicon doesn't define is two words to Synonyms and Define: "river-wood"
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

    // The menus ask again each frame while it's null
    internal List<string>? Suggest(string word)
    {
        if (!Speller.Loaded || string.IsNullOrWhiteSpace(word))
            return [];

        lock (_suggesting)
        {
            DropStale(_suggesting, ref _suggestingStamp, MostSuggesting);

            if (!_suggesting.TryGetValue(word, out var lookup))
                _suggesting[word] = lookup = Task.Run(() => Lookup(word, _settings.MaximumSuggestions));

            return lookup.IsCompleted ? lookup.Result : null;
        }
    }

    // Logged here: the plugins asking swallow a gate's throw
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

    // Still true when the save throws: the word is in, and the next save keeps it
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

    // Funcs, not actions, as every IPC caller uses InvokeFunc
    internal bool Ignore(string word)
    {
        Speller.Ignore(word);
        return true;
    }

    internal bool Define(string word, string original, Action<string>? use)
    {
        _openDefine(word, original, use);
        return true;
    }

    private const int MostSynonyms = 10;

    // DefineWindow.Spell matches the case
    internal List<string> Synonyms(string word) =>
    [
        .. Lexicon.Synonyms(_lexicon, word)
            .SelectMany(group => group.Split(", "))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MostSynonyms),
    ];

    public void Dispose()
    {
        Svc.Pi.UiBuilder.Draw -= Announce;
        foreach (var gate in new ICallGateProvider[] { _apiVersion, _check, _suggest, _suggestNow, _isWord, _addToDictionary, _ignore, _wordAt, _synonyms, _define, _lookup })
            gate.UnregisterFunc();

        // After unregistering, so the boxes find the gates gone and drop their marks
        _available.SendMessage();
    }
}
