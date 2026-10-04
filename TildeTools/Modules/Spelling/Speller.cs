using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WeCantSpell.Hunspell;

namespace TildeTools.Modules.Spelling;

// All under _sync, filled from background threads while the boxes check on the draw thread.
internal static class Speller
{
    // Checks and Suggest share it, so a Suggest's 100 ms and more doesn't hold up a check on the draw thread.
    private static readonly ReaderWriterLockSlim _sync = new();

    private static WordList? _primary;
    private static WordList? _alternate;

    private static WordList? _us;

    private static readonly HashSet<string> _custom = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _game = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase);

    // chat.txt's terms are accepted but never suggested.
    private static readonly HashSet<string> _accepted = new(StringComparer.OrdinalIgnoreCase);

    // Names, both the ones from Lumina and people's, are accepted but never officially corrections. Rank offers them to finish a capitalized word though.
    private static readonly HashSet<string> _gameNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _people = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _offered = new(StringComparer.OrdinalIgnoreCase);

    // Sorted OrdinalIgnoreCase, for Starting: the game's names sort once they're in, the people offered as they change.
    private static string[] _gameSorted = [], _offeredSorted = [], _roots = [];

    private static readonly HashSet<string> _inserted = new(StringComparer.Ordinal);

    private static int _loadToken;

    // Structs, so a using doesn't box.
    private static Held Reading()
    {
        _sync.EnterReadLock();
        return new(false);
    }

    private static Held Writing()
    {
        _sync.EnterWriteLock();
        return new(true);
    }

    private readonly struct Held(bool write) : IDisposable
    {
        public void Dispose()
        {
            if (write)
                _sync.ExitWriteLock();
            else
                _sync.ExitReadLock();
        }
    }

    internal static int Generation { get; private set; }

    // No lock since the menus ask every frame.
    internal static bool Loaded => Volatile.Read(ref _primary) is not null;

    // Both lists take ~150 ms.
    // Not published after an Unload. Otherwise, a stale load holds ~20 MB with the module off.
    internal static Task<string?> Load(string directory, bool british, IEnumerable<string> custom, IEnumerable<string> accepted)
    {
        int token;
        using (Writing())
        {
            token = ++_loadToken;

            // Seeded now, so a word added or removed while the lists load carries into them.
            _custom.UnionWith(custom);
        }

        List<string> terms = [.. accepted];

        return Task.Run(() =>
        {
            var (name, other) = british ? ("en_GB", "en_US") : ("en_US", "en_GB");
            var primary = WordList.CreateFromFiles(Path.Combine(directory, $"{name}.dic"), Path.Combine(directory, $"{name}.aff"));
            var alternate = WordList.CreateFromFiles(Path.Combine(directory, $"{other}.dic"), Path.Combine(directory, $"{other}.aff"));

            // Taken before any word is added. RootWords can only be walked, its CopyTo throws and ToArray uses CopyTo.
            HashSet<string> seen = new(primary.RootWords, StringComparer.OrdinalIgnoreCase);
            seen.UnionWith(alternate.RootWords);

            string[] roots = [.. seen.Order(StringComparer.OrdinalIgnoreCase)];

            // Added to the new list before it's published, otherwise the game words can hold up checks if we do it under the lock.
            // ~75k words is ~200ms
            HashSet<string> had;
            using (Reading())
                had = new(_game.Except(_gameNames), StringComparer.OrdinalIgnoreCase);

            foreach (var word in had)
                if (!primary.Check(word))
                    _ = primary.Add(word);

            using (Writing())
            {
                if (token != _loadToken)
                    return null;

                (_primary, _alternate, _us, _roots) = (primary, alternate, british ? alternate : primary, roots);
                _accepted.Clear();
                _accepted.UnionWith(terms);
                _inserted.Clear();

                foreach (var word in _game.Where(w => !had.Contains(w) && !_gameNames.Contains(w)))
                    Introduce(word, null);

                foreach (var word in _custom)
                    Introduce(word, _inserted);

                Generation++;
            }

            return $"Loaded the {name} dictionary, {primary.RootCount} root words, with {other} accepted alongside it.";
        });
    }

    internal static void Unload()
    {
        using (Writing())
        {
            (_primary, _alternate, _us, _gameSorted, _offeredSorted, _roots) = (null, null, null, [], [], []);
            _custom.Clear();
            _game.Clear();
            _gameNames.Clear();
            _ignored.Clear();
            _accepted.Clear();
            _people.Clear();
            _offered.Clear();
            _inserted.Clear();
            Generation++;
            _loadToken++;
        }
    }

    internal static bool IsWord(string word)
    {
        var trimmed = word.Trim();

        using (Reading())
            return Known(trimmed) || trimmed.Split('-', '–', '—') is { Length: > 1 } halves && halves.All(half => half.Length == 0 || Known(half));
    }

    // Names never go into the lists, so for a name this is either the dictionaries' word or the user's.
    internal static bool InDictionary(string word)
    {
        using (Reading())
            return In(_primary, word) || In(_alternate, word);
    }

    // No lock, caller holds _sync. preconditional so several of the checks see the same lists.
    private static bool Known(string word) =>
        _custom.Contains(word) || _game.Contains(word) || _people.Contains(word) || _ignored.Contains(word) || _accepted.Contains(word) || In(_primary, word) || In(_alternate, word);

    // Checked as it's typed and THEN normalized to lowercase, so "Paris" passes and "paris" doesn't.
    private static bool In(WordList? list, string word) =>
        list is not null && (list.Check(word) || (word.ToLowerInvariant() is var lower && lower != word && list.Check(lower)));

    // Called off the game's thread to save ~100+ms. This way it computes its suggestions in the background and we don't have to deal with hitches.
    internal static List<string> Suggest(string word, int limit)
    {
        using (Reading())
            return _primary is null || word.Length == 0 ? [] : Rank(word, _primary.Suggest(word), _alternate!.Suggest(word), limit);
    }

    internal static bool AddWord(string word)
    {
        var trimmed = word.Trim();
        if (trimmed.Length == 0)
            return false;

        using (Writing())
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

        using (Writing())
        {
            _ = _custom.Remove(trimmed);

            // A game word stays, but a name is never a correction.
            if (_inserted.Remove(trimmed) && (!_game.Contains(trimmed) || _gameNames.Contains(trimmed)))
                _ = _primary?.Remove(trimmed);

            Generation++;
        }
    }

    internal static void Ignore(string word)
    {
        var trimmed = word.Trim();
        if (trimmed.Length == 0)
            return;

        using (Writing())
        {
            _ = _ignored.Add(trimmed);
            Generation++;
        }
    }

    // Locked per batch so the draw thread never waits on the whole set.
    // False when wanted also turned false partway.
    // The creator's random names are accepted but, again, should never be suggested.
    internal static bool AddGameWords(IEnumerable<string> words, Dictionary<string, GameName> names, Func<bool> wanted)
    {
        List<string> completable = [];

        foreach (var batch in words.Chunk(512))
        {
            using (Writing())
            {
                if (!wanted())
                    return false;

                foreach (var word in batch)
                {
                    _ = _game.Add(word);

                    if (!names.TryGetValue(word, out var name))
                        Introduce(word, null);
                    else if (_gameNames.Add(word) && name.Kind != NameKind.Creator)
                        completable.Add(word);
                }

                Generation++;
            }
        }

        // Completions cached before the sort would stay without the names so we regenerate this.
        using (Writing())
        {
            if (!wanted())
                return false;

            _gameSorted = [.. _gameSorted.Concat(completable).Order(StringComparer.OrdinalIgnoreCase)];
            Generation++;
        }

        return true;
    }

    internal static void SetPeople(IEnumerable<string> offered, IEnumerable<string> accepted)
    {
        static HashSet<string> Names(IEnumerable<string> names) => new(names, StringComparer.OrdinalIgnoreCase);
        var (offering, wanted) = (Names(offered), Names(offered.Concat(accepted)));

        using (Writing())
        {
            if (wanted.SetEquals(_people) && offering.SetEquals(_offered))
                return;

            _people.Clear();
            _people.UnionWith(wanted);
            _offered.Clear();
            _offered.UnionWith(offering);
            _offeredSorted = [.. _offered.Order(StringComparer.OrdinalIgnoreCase)];
            Generation++;
        }
    }

    // Caller holds _sync!
    // Never a duplicate, or removing it later would take the real word too.
    // into: where it's recorded as added, so it can come out again.
    private static void Introduce(string word, HashSet<string>? into)
    {
        if (_primary is null || _primary.Check(word))
            return;

        if (_primary.Add(word))
            _ = into?.Add(word);
    }

    // Caller holds, you guessed it, _sync
    // Not a plain Interleave, which puts en_GB's rare words every other place (Gridan gets Grid an, Gradin, Grid-an, Gridania)
    private static List<string> Rank(string word, IEnumerable<string> first, IEnumerable<string> second, int limit)
    {
        var merged = Interleave(first, second);

        // Hunspell suggests "Grid-an" beside "Grid an", and stray-letter splits like "reals e"
        bool Dropped(string s) => s.Split(' ', '-') is { Length: > 1 } parts
            && (s.Contains('-') && merged.Contains(s.Replace('-', ' '))
                || parts.Any(p => p.Length == 1 && p is not ("a" or "A" or "I")));

        bool Completes(string s) => word.Length >= 4 && char.IsUpper(s[0]) && !s.Contains(' ')
            && s.StartsWith(word, StringComparison.OrdinalIgnoreCase);

        // s is the word with two neighboring letters traded, as "Their" is for "Thier"
        bool Swapped(string s) => s.Length == word.Length && word.AsSpan().CommonPrefixLength(s) is var at && at + 1 < s.Length
            && char.ToUpperInvariant(s[at]) == char.ToUpperInvariant(word[at + 1]) && char.ToUpperInvariant(s[at + 1]) == char.ToUpperInvariant(word[at])
            && s.AsSpan(at + 2).Equals(word.AsSpan(at + 2), StringComparison.OrdinalIgnoreCase);

        // A name only when it's plainly what's being typed.
        // It must be capitalized, four letters or more into the word, and no dictionary word starts the same way.
        // "Gridan" is Gridania, but "Thes" could be "these"
        var names = word.Length >= 4 && char.IsUpper(word[0]) && !Starting(_roots, word).Any() ? Starting(_offeredSorted, word).Concat(Starting(_gameSorted, word)).Distinct(StringComparer.OrdinalIgnoreCase).Take(1) : [];

        List<string> kept = [.. merged.Where(s => !Dropped(s))];

        // A common word that's a swap leads ahead of the name
        var swaps = kept.Where(s => Swapped(s) && Common(s));

        // The alternate list has no game words added, so it puts a dictionary word before a game one.
        return [.. swaps.Concat(names).Concat(kept.OrderBy(s => Completes(s) ? In(_alternate, s) ? 0 : 1 : Common(s) ? 2 : 3))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(limit)];
    }

    // From a list sorted OrdinalIgnoreCase, where a word's own longer forms follow it and are skipped. Ergo, Gridania, not Gridania's or Gridanian.
    private static IEnumerable<string> Starting(string[] sorted, string prefix)
    {
        string? root = null;
        var at = Array.BinarySearch(sorted, prefix, StringComparer.OrdinalIgnoreCase);

        for (at = at < 0 ? ~at : at; at < sorted.Length && sorted[at].StartsWith(prefix, StringComparison.OrdinalIgnoreCase); at++)
            if (root is null || !sorted[at].StartsWith(root, StringComparison.OrdinalIgnoreCase))
                yield return root = sorted[at];
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

    // "Where do you think THIS caller's hold goes? That's right! It goes in the _sync hole!"
    private static bool Common(string word)
    {
        if (word.Split(' ', '-') is { Length: > 1 } parts)
            return parts.All(Common);

        return _game.Contains(word) || _people.Contains(word) || _custom.Contains(word)
            || _us!.Check(word) || Spellings.Any(s => word.IndexOf(s.British, StringComparison.OrdinalIgnoreCase) is var at and >= 0
                && _us!.Check(word[..at] + s.American + word[(at + s.British.Length)..]));
    }
}
