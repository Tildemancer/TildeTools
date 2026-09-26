using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WeCantSpell.Hunspell;

namespace TildeTools.Modules.Spelling;

// All under _sync, filled from background threads while the boxes check on the draw thread
internal static class Speller
{
    private static readonly object _sync = new();

    private static WordList? _primary;
    private static WordList? _alternate;

    private static WordList? _us;

    private static readonly HashSet<string> _custom = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _game = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase);

    // chat.txt's terms: accepted, never suggested
    private static readonly HashSet<string> _accepted = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> _people = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _offered = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _peopleInserted = new(StringComparer.Ordinal);

    private static readonly HashSet<string> _inserted = new(StringComparer.Ordinal);

    private static int _loadToken;

    internal static int Generation { get; private set; }

    // No lock: the menus ask every frame, and Suggest holds _sync 100 ms and more
    internal static bool Loaded => Volatile.Read(ref _primary) is not null;

    // Both lists take about 150 ms
    // Not published after an Unload, or a stale load holds ~20 MB with the module off
    internal static Task<string?> Load(string directory, bool british, IEnumerable<string> custom, IEnumerable<string> accepted)
    {
        int token;
        lock (_sync)
            token = ++_loadToken;

        List<string> words = [.. custom];
        List<string> terms = [.. accepted];

        return Task.Run(() =>
        {
            var (name, other) = british ? ("en_GB", "en_US") : ("en_US", "en_GB");
            var primary = WordList.CreateFromFiles(Path.Combine(directory, $"{name}.dic"), Path.Combine(directory, $"{name}.aff"));
            var alternate = WordList.CreateFromFiles(Path.Combine(directory, $"{other}.dic"), Path.Combine(directory, $"{other}.aff"));

            // Onto the new list before it's published
            // Under the lock, 74.6k game words held a box's check up to 188 ms
            HashSet<string> had;
            lock (_sync)
                had = new(_game, StringComparer.OrdinalIgnoreCase);

            foreach (var word in had)
                if (!primary.Check(word))
                    _ = primary.Add(word);

            lock (_sync)
            {
                if (token != _loadToken)
                    return null;

                (_primary, _alternate, _us) = (primary, alternate, british ? alternate : primary);
                _accepted.Clear();
                _accepted.UnionWith(terms);
                _inserted.Clear();

                foreach (var word in _game.Where(w => !had.Contains(w)))
                    Introduce(word, null);

                foreach (var word in words)
                {
                    _ = _custom.Add(word);
                    Introduce(word, _inserted);
                }

                _peopleInserted.Clear();
                Offer();
                Generation++;
            }

            return $"Loaded the {name} dictionary, {primary.RootCount} root words, with {other} accepted alongside it.";
        });
    }

    internal static void Unload()
    {
        lock (_sync)
        {
            (_primary, _alternate, _us) = (null, null, null);
            _custom.Clear();
            _game.Clear();
            _ignored.Clear();
            _accepted.Clear();
            _people.Clear();
            _offered.Clear();
            _peopleInserted.Clear();
            _inserted.Clear();
            Generation++;
            _loadToken++;
        }
    }

    internal static bool IsWord(string word)
    {
        var trimmed = word.Trim();

        lock (_sync)
            return Known(trimmed) || trimmed.Split('-', '–', '—') is { Length: > 1 } halves && halves.All(half => half.Length == 0 || Known(half));
    }

    // Caller holds _sync
    private static bool Known(string word) =>
        _custom.Contains(word) || _game.Contains(word) || _people.Contains(word) || _ignored.Contains(word) || _accepted.Contains(word) || In(_primary, word) || In(_alternate, word);

    // Not lowercase alone: "Paris" passes and "paris" doesn't
    private static bool In(WordList? list, string word) =>
        list is not null && (list.Check(word) || (word.ToLowerInvariant() is var lower && lower != word && list.Check(lower)));

    // 100 ms and more, measured, so callers keep it off the game's thread
    internal static List<string> Suggest(string word, int limit)
    {
        lock (_sync)
            return _primary is null || word.Length == 0 ? [] : Rank(word, _primary.Suggest(word), _alternate!.Suggest(word), limit);
    }

    internal static bool AddWord(string word)
    {
        var trimmed = word.Trim();
        if (trimmed.Length == 0)
            return false;

        lock (_sync)
        {
            if (!_custom.Add(trimmed))
                return false;

            Introduce(trimmed, _inserted);
            Generation++;
        }

        return true;
    }

    internal static void RemoveWord(string word)
    {
        var trimmed = word.Trim();

        lock (_sync)
        {
            _ = _custom.Remove(trimmed);

            if (_inserted.Remove(trimmed) && !_game.Contains(trimmed))
            {
                // Still offered: SetPeople takes it out when the name goes
                if (_offered.Contains(trimmed))
                    _ = _peopleInserted.Add(trimmed);
                else
                    _ = _primary?.Remove(trimmed);
            }

            Generation++;
        }
    }

    internal static void Ignore(string word)
    {
        var trimmed = word.Trim();
        if (trimmed.Length == 0)
            return;

        lock (_sync)
        {
            _ = _ignored.Add(trimmed);
            Generation++;
        }
    }

    // Locked per batch so the draw thread never waits on the whole set
    // False when wanted turned false partway
    internal static bool AddGameWords(IEnumerable<string> words, Func<bool>? wanted = null)
    {
        foreach (var batch in words.Chunk(512))
        {
            lock (_sync)
            {
                if (wanted?.Invoke() == false)
                    return false;

                foreach (var word in batch)
                {
                    _ = _game.Add(word);
                    Introduce(word, null);
                }

                Generation++;
            }
        }

        return true;
    }

    internal static void SetPeople(IEnumerable<string> offered, IEnumerable<string> accepted)
    {
        static HashSet<string> Names(IEnumerable<string> names) => new(names, StringComparer.OrdinalIgnoreCase);
        var (offering, wanted) = (Names(offered), Names(offered.Concat(accepted)));

        lock (_sync)
        {
            if (wanted.SetEquals(_people) && offering.SetEquals(_offered))
                return;

            // Still the user's word: RemoveWord takes it out
            foreach (var name in _peopleInserted.Where(n => !_game.Contains(n)))
                if (_custom.Contains(name))
                    _ = _inserted.Add(name);
                else
                    _ = _primary?.Remove(name);

            _peopleInserted.Clear();
            _people.Clear();
            _people.UnionWith(wanted);
            _offered.Clear();
            _offered.UnionWith(offering);
            Generation++;

            Offer();
        }
    }

    // Caller holds _sync
    private static void Offer()
    {
        foreach (var name in _offered)
            Introduce(name, _peopleInserted);
    }

    // Caller holds _sync
    // Never a duplicate: removing it later would take the real word too
    // into: where it's recorded as added, so it can come out again
    private static void Introduce(string word, HashSet<string>? into)
    {
        if (_primary is null || _primary.Check(word))
            return;

        if (_primary.Add(word))
            _ = into?.Add(word);
    }

    // Not a plain Interleave, which puts en_GB's rare words every other place: Gridan gets Grid an, Gradin, Grid-an, Gridania
    // Caller holds _sync
    private static List<string> Rank(string word, IEnumerable<string> first, IEnumerable<string> second, int limit)
    {
        var merged = Interleave(first, second);

        // Hunspell suggests "Grid-an" beside "Grid an", and stray-letter splits like "reals e"
        bool Dropped(string s) => s.Split(' ', '-') is { Length: > 1 } parts
            && (s.Contains('-') && merged.Contains(s.Replace('-', ' '))
                || parts.Any(p => p.Length == 1 && p is not ("a" or "A" or "I")));

        bool Completes(string s) => word.Length >= 4 && char.IsUpper(s[0]) && !s.Contains(' ')
            && s.StartsWith(word, StringComparison.OrdinalIgnoreCase);

        return [.. merged.Where(s => !Dropped(s))
            .OrderBy(s => Completes(s) ? 0 : Common(s) ? 1 : 2)
            .Take(limit)];
    }

    private static List<string> Interleave(IEnumerable<string> first, IEnumerable<string> second)
    {
        List<string> merged = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        using var a = first.GetEnumerator();
        using var b = second.GetEnumerator();

        for (bool moreA = true, moreB = true; moreA || moreB;)
        {
            if (moreA && (moreA = a.MoveNext()) && seen.Add(a.Current))
                merged.Add(a.Current);

            if (moreB && (moreB = b.MoveNext()) && seen.Add(b.Current))
                merged.Add(b.Current);
        }

        return merged;
    }

    private static readonly (string British, string American)[] Spellings =
    [
        ("our", "or"), ("tre", "ter"), ("ise", "ize"), ("isa", "iza"), ("yse", "yze"), ("ogue", "og"),
        ("ence", "ense"), ("ll", "l"), ("ae", "e"), ("oe", "e"), ("mme", "m"), ("grey", "gray"),
    ];

    // Caller holds _sync
    private static bool Common(string word)
    {
        if (word.Split(' ', '-') is { Length: > 1 } parts)
            return parts.All(Common);

        return _game.Contains(word) || _people.Contains(word) || _custom.Contains(word)
            || _us!.Check(word) || Spellings.Any(s => word.IndexOf(s.British, StringComparison.OrdinalIgnoreCase) is var at and >= 0
                && _us!.Check(word[..at] + s.American + word[(at + s.British.Length)..]));
    }
}
