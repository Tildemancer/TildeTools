using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TildeTools.Modules.Spelling;

// This comes from chat.txt, so no Wiktionary credits here.
internal readonly record struct Sense(string Word, string Pos, string Gloss, bool Own = false);

internal readonly record struct Entry(string Word, List<Sense> Senses);

internal readonly record struct Term(string[] Spellings, List<(string Pos, string Gloss)> Meanings);

// Binary searched on disk, so nothing is held between lookups.
// Lines are "key<TAB>item<US>item", keys lowercase/in order.
internal static class Lexicon
{
    internal const char Unit = '\u001F';
    internal const char Record = '\u001E';

    // Strongest first, then a sense so the tag exposes itself.
    // "(slang) " before its gloss, and " (slang)" after its synonyms
    internal static readonly string[] Registers = ["slur", "offensive", "vulgar", "derogatory", "slang"];

    // Strips the label before we use it or look it up.
    internal static string Bare(string synonym) =>
        Array.Find(Registers, register => synonym.EndsWith($" ({register})", StringComparison.Ordinal)) is { } found ? synonym[..^(found.Length + 3)] : synonym;

    // "Potentially NSFW" collapsible line containing the aforementioned slur/vulgar/offensive trinity within them. Also it excludes them from menus
    internal static bool Closed(string text) =>
        Registers.Take(3).Any(register => text.StartsWith($"({register}) ", StringComparison.Ordinal) || text.EndsWith($" ({register})", StringComparison.Ordinal));

    // Drop possessives among others so, say, 'Alphinaud's' looks up 'Alphinaud'.
    internal static string Stem(string word) => word is [_, .., '\'' or '’', 's'] ? word[..^2] : word;

    // Tabs come out in file order and definitions.tsv lists the lowercase first, ala "paris" before "Paris".
    internal static List<Entry> Define(string directory, string word)
    {
        var path = Path.Combine(directory, "definitions.tsv");
        var key = word.ToLowerInvariant();
        List<(Entry Entry, List<string> Bases)> found = [];

        foreach (var (spellings, meanings) in Terms(directory))
        {
            if (meanings.Count == 0 || (Array.Find(spellings, s => s == word) ?? Array.Find(spellings, s => s.Equals(word, StringComparison.OrdinalIgnoreCase))) is not { } spelling)
                continue;

            found.Add((new Entry(spelling, [.. meanings.Select(m => new Sense(spelling, m.Pos, m.Gloss, Own: true))]), []));
        }

        foreach (var item in Items(path, key))
        {
            var (sense, target) = Parse(item, key);

            var at = found.FindIndex(f => f.Entry.Word == sense.Word);
            if (at < 0)
            {
                at = found.Count;
                found.Add((new Entry(sense.Word, []), []));
            }

            found[at].Entry.Senses.Add(sense);

            if (target is not null && !target.Equals(key, StringComparison.OrdinalIgnoreCase) && !found[at].Bases.Contains(target))
                found[at].Bases.Add(target);
        }

        // The base keeps its case, so cats takes cat's senses and not Cat's or CAT's from the same line.
        foreach (var (entry, bases) in found)
            foreach (var target in bases)
                entry.Senses.AddRange(Items(path, target).Select(item => Parse(item, target.ToLowerInvariant()).Sense).Where(sense => sense.Word == target));

        return [.. found.Select(f => f.Entry)];
    }

    internal static List<Term> Terms(string directory)
    {
        var path = Path.Combine(directory, "chat.txt");
        List<Term> terms = [];

        if (!File.Exists(path))
            return terms;

        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
                continue;

            if (!line.StartsWith("  ", StringComparison.Ordinal))
                terms.Add(new([.. line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)], []));
            else if (terms.Count > 0 && line.IndexOf(": ", StringComparison.Ordinal) is var colon and > 2)
                terms[^1].Meanings.Add((line[2..colon], line[(colon + 2)..]));
        }

        return terms;
    }

    // A group per WordNet sense, then one of Wiktionary's extras.
    // A phrase that repeats the word swaps in badly, like "make garden", "garden variety" or "bush-league".
    internal static string[][] Synonyms(string directory, string word) =>
    [
        .. Items(Path.Combine(directory, "synonyms.tsv"), word)
            .Select(group => group.Split(", ").Where(synonym => !synonym.Split(' ', '-').Contains(word, StringComparer.OrdinalIgnoreCase)).ToArray())
            .Where(group => group.Length > 0),
    ];

    // Real-world politics are a no-go: listed with nothing, so not looked up online either. Technically negotiable, I guess...
    internal static bool LeftOut(string directory, string word) => Find(Path.Combine(directory, "definitions.tsv"), word) is "";

    private static string[] Items(string path, string word) =>
        Find(path, word) is { Length: > 0 } line ? line.Split(Unit) : [];

    // "[Headword ]pos: gloss", then <RS> and the base word for a form.
    private static (Sense Sense, string? Target) Parse(string item, string word)
    {
        var (text, target) = item.IndexOf(Record) is var at and >= 0 ? (item[..at], item[(at + 1)..]) : (item, null);
        var colon = text.IndexOf(": ", StringComparison.Ordinal);
        var head = colon < 0 ? "" : text[..colon];
        var space = head.LastIndexOf(' ');

        return (new Sense(space < 0 ? word : head[..space], head[(space + 1)..], text[(colon < 0 ? 0 : colon + 2)..]), target);
    }

    // The key's line, if there is one, starts in lo..hi, and lo is always a line's start.
    // Compared as the files are sorted. Ordinal on the decoded text.
    // ’ reads as ', like the dictionaries' ICONV does, because the files' keys only use '
    private static string? Find(string path, string word)
    {
        if (!File.Exists(path))
            return null;

        var key = word.Replace('’', '\'').ToLowerInvariant();

        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);

        for (long lo = 0, hi = file.Length; lo < hi;)
        {
            var mid = lo + (hi - lo) / 2;
            var start = LineStart(file, mid);

            // No line starts in mid..hi
            if (start >= hi)
            {
                hi = mid;
                continue;
            }

            var line = ReadLine(file, start, out var next);
            var tab = line.IndexOf('\t');
            var order = string.CompareOrdinal(tab < 0 ? line : line[..tab], key);

            if (order == 0)
                return line[(tab + 1)..];

            if (order < 0)
                lo = next;
            else
                hi = start;
        }

        return null;
    }

    // The first line start at, or after at.
    private static long LineStart(FileStream file, long at)
    {
        if (at == 0)
            return 0;

        file.Position = at - 1;
        while (file.ReadByte() is >= 0 and not '\n')
        {
        }

        return file.Position;
    }

    private static string ReadLine(FileStream file, long start, out long next)
    {
        file.Position = start;
        using var bytes = new MemoryStream();

        for (var b = file.ReadByte(); b is >= 0 and not '\n'; b = file.ReadByte())
            bytes.WriteByte((byte)b);

        next = file.Position;
        return Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
    }
}
