using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Lumina;
using Lumina.Data;
using Lumina.Data.Files.Excel;
using Lumina.Data.Structs.Excel;
using Lumina.Extensions;
using Lumina.Text.ReadOnly;
using Origins = System.Collections.Generic.Dictionary<string, TildeTools.Modules.Spelling.GameName>.AlternateLookup<System.ReadOnlySpan<char>>;
using Seen = System.Collections.Generic.Dictionary<string, (int Count, int Shortest, int Proper, int Lower)>.AlternateLookup<System.ReadOnlySpan<char>>;

namespace TildeTools.Modules.Spelling;

internal static class GameText
{
    // In letters. "gil" is three!
    private const int ShortestWord = 3;

    // In words. "Plate Pauldron" is two.
    private const int NameSized = 4;

    // According to the clanker, names are 0-2.3% lowercase ("a titan"), words 66% up (moogle, chocobo, these). I'm going to trust its math here and cut it at 5.
    private const int MostLowercasePercent = 5;

    private static readonly string[] NameSheets = ["ENpcResident", "BNpcName", "PlaceName"];

    // CharaMakeName's text columns in order, from Lumina's names.
    // Its six Unknowns are my best guess.
    private static readonly string[] GeneratorLists =
    [
        "Midlander male forename", "Midlander female forename", "Midlander surname",
        "Highlander male forename", "Highlander female forename", "Highlander surname",
        "Elezen male forename", "Elezen female forename", "Wildwood surname", "Duskwight surname",
        "Seeker of the Sun male forename", "Seeker of the Sun female forename", "Seeker of the Sun male surname", "Seeker of the Sun female surname",
        "Keeper of the Moon male forename", "Keeper of the Moon female forename", "Keeper of the Moon surname",
        "Plainsfolk name part", "Plainsfolk name part", "Plainsfolk name part",
        "Dunesfolk male forename", "Dunesfolk male surname", "Dunesfolk female forename", "Dunesfolk female surname",
        "Sea Wolf male forename", "Sea Wolf male surname", "Sea Wolf female forename", "Sea Wolf female surname",
        "Hellsguard forename", "Hellsguard male surname", "Hellsguard female surname",
        "Raen male forename", "Raen female forename", "Raen surname", "Xaela male forename", "Xaela female forename", "Xaela surname",
        "Helion forename", "Helion surname", "Lost forename", "Lost surname", "Hrothgar name", "Hrothgar name", "Hrothgar name",
        "Veena forename", "Rava surname", "Veena surname", "Rava female forename", "Rava surname", "Veena name",
    ];

    // Names are considered to be "words the game capitalizes where a capital means something and hardly ever lowercases", and whole names for a phrase looked up.
    // Not through ExcelModule, which keeps every sheet it opens (255 MB for all)
    // Lumina's file cache only holds weak references.
    internal static (List<string> Words, Dictionary<string, GameName> Names) Read(GameData data)
    {
        // Counted first, so a word's whole name is the one the game uses most: "Wuk Lamat" over "Wuk Evu"
        Dictionary<string, int> uses = new(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in NameSheets)
            EachCell(data, sheet, firstOnly: true, (text, _) => uses[text] = uses.GetValueOrDefault(text) + 1);

        // How many whole names each word is part of.
        Dictionary<string, int> shared = new(StringComparer.OrdinalIgnoreCase);
        foreach (var full in uses.Keys)
            foreach (var part in full.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase))
                shared[part] = shared.GetValueOrDefault(part) + 1;

        Dictionary<string, (int Count, int Shortest, int Proper, int Lower)> seen = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, GameName> origins = new(StringComparer.OrdinalIgnoreCase);
        var (lookup, found) = (seen.GetAlternateLookup<ReadOnlySpan<char>>(), origins.GetAlternateLookup<ReadOnlySpan<char>>());

        // RacingChocoboName is left out
        foreach (var name in data.Excel.SheetNames.Where(sheet => sheet != "RacingChocoboName"))
        {
            // Only the first text column is the name, the rest are plurals and titles.
            // When it comes to the character creator's sheet, every column is a name.
            NameKind? kind = name switch
            {
                "ENpcResident" or "BNpcName" => NameKind.Npc,
                "PlaceName" => NameKind.Place,
                "CharaMakeName" => NameKind.Creator,
                _ => null,
            };

            EachCell(data, name, firstOnly: false, (text, column) =>
                Take(lookup, text, kind is not { } k || k != NameKind.Creator && column > 0 ? null
                    : new Origin(k, k == NameKind.Creator ? GeneratorLists.ElementAtOrDefault(column) ?? "name" : text, found, uses)));
        }

        // A one-off misspelling in prose is either slurring or a typo, like "latesht" or "acheive"
        // One in a short cell needs more than a sentence's opening capital, ala "Ahhh... Tes... Tesleen..."
        List<string> words = [.. seen.Where(p => p.Value.Count >= 2 || p.Value.Shortest <= NameSized && p.Value.Proper + p.Value.Lower > 0).Select(p => p.Key)];

        Dictionary<string, GameName> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (var word in words)
            if (seen[word] is { Proper: > 0 } tally && tally.Lower * 100 < MostLowercasePercent * (tally.Proper + tally.Lower))
                if (origins.TryGetValue(word, out var origin))
                    names[word] = origin with { Others = Math.Max(0, shared.GetValueOrDefault(word) - 1) };
                // I'm, I've and I'll are capitalized for I, not as names, so this is my best attempt to filter those out.
                else if (word is not ['I' or 'i', '\'' or '’', ..])
                    names[word] = new(NameKind.Other, word, Seen: tally.Proper + tally.Lower);

        foreach (var (full, origin) in origins.Where(o => o.Key.Contains(' ')))
            names.TryAdd(full, origin);

        return (words, names);
    }

    // Take: a cell's text, and which of the sheet's text columns that it's in.
    private static void EachCell(GameData data, string name, bool firstOnly, Action<string, int> take)
    {
        if (data.GetFile<ExcelHeaderFile>($"exd/{name}.exh") is not { } header)
            return;

        // Chosen the same way ExcelModule.GetSheet does.
        int[] columns = [.. header.ColumnDefinitions.Where(c => c.Type == ExcelColumnDataType.String).Select(c => (int)c.Offset)];
        var suffix = header.Languages.Contains(Language.English) ? "_en" : header.Languages.Contains(Language.None) ? "" : null;
        if (columns.Length == 0 || suffix is null)
            return;

        if (firstOnly)
            columns = columns[..1];

        // A row is a 6-byte header,
        // then DataOffset bytes (per subrow, after a 2-byte id),
        // then the strings those bytes point into.
        var subrows = header.Header.Variant == ExcelVariant.Subrows;
        var id = subrows ? 2 : 0;
        var stride = id + header.Header.DataOffset;

        foreach (var page in header.DataPages)
        {
            if (data.GetFile<ExcelDataFile>($"exd/{name}_{page.StartId}{suffix}.exd") is not { } file)
                continue;

            var bytes = file.Data;

            foreach (var row in file.RowData.Values)
            {
                var at = (int)row.Offset;
                var count = subrows ? BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at + 4)) : 1;
                var strings = at + 6 + count * stride;

                for (var i = 0; i < count; i++)
                {
                    for (var column = 0; column < columns.Length; column++)
                    {
                        var text = bytes.AsSpan(strings + (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 6 + i * stride + id + columns[column])));
                        var cell = new ReadOnlySeStringSpan(text[..text.IndexOf((byte)0)]);

                        // Skips "rsv" cells, since their text comes from the server later.
                        // Resolving it means reading a table Dalamud fills on the game thread with no lock from our background thread.
                        take(cell.IsRsv() ? "" : cell.ExtractText(), column);
                    }
                }
            }
        }
    }

    // A name cell, whose words and whole name are noted for the Define window.
    private readonly record struct Origin(NameKind Kind, string Full, Origins Found, Dictionary<string, int> Uses);

    // Works on spans so only a word not seen before becomes a string.
    // Splitting made ~1 GB of garbage per load.
    private static void Take(Seen seen, string text, Origin? origin)
    {
        var span = text.AsSpan();
        if (!span.Contains(' ') && span.IndexOfAny(PathMarks) >= 0)
            return;

        var tokens = 0;
        foreach (var range in span.SplitAny(WordBreaks))
            tokens += span[range].IsEmpty ? 0 : 1;

        // A label, ala one word with no full stop, is capitalized as a name is.
        //  "Excalibur", not "Hmph."
        var label = tokens == 1 && span.TrimEnd() is [.., var last] && Array.IndexOf(Stops, last) < 0;

        foreach (var range in span.SplitAny(WordBreaks))
        {
            var token = span[range].Trim(Trimmed);
            var meaningful = label || origin is not null || !OpensSentence(span, range.Start.GetOffset(span.Length));

            // As WithHalves, only a first half opens a sentence.
            Learn(token, meaningful);

            if (token.Contains('-'))
                foreach (var half in token.Split('-'))
                    Learn(token[half], meaningful || half.Start.Value > 0);
        }

        if (origin is { } whole && tokens > 1)
            Name(whole, whole.Full);

        void Learn(ReadOnlySpan<char> word, bool meaningful)
        {
            if (!IsWorthLearning(word))
                return;

            ref var was = ref CollectionsMarshal.GetValueRefOrAddDefault(seen, word, out var known);
            was = (was.Count + 1, known ? Math.Min(was.Shortest, tokens) : tokens,
                was.Proper + (meaningful && char.IsUpper(word[0]) ? 1 : 0), was.Lower + (char.IsLower(word[0]) ? 1 : 0));

            if (origin is { } named)
                Name(named, word);
        }
    }

    // A whole name over part of one, then the higher kind, then the whole name that gets used most.
    private static void Name(Origin origin, ReadOnlySpan<char> key)
    {
        ref var was = ref CollectionsMarshal.GetValueRefOrAddDefault(origin.Found, key, out var known);
        if (!known || Better(key, origin, was))
            was = new(origin.Kind, origin.Full);
    }

    private static bool Better(ReadOnlySpan<char> key, Origin origin, GameName was)
    {
        var (whole, wasWhole) = (key.Equals(origin.Full, StringComparison.OrdinalIgnoreCase), key.Equals(was.Full, StringComparison.OrdinalIgnoreCase));
        return whole != wasWhole ? whole
            : origin.Kind != was.Kind ? origin.Kind > was.Kind
            : origin.Uses.GetValueOrDefault(origin.Full) > origin.Uses.GetValueOrDefault(was.Full);
    }

    // A capital after a full stop. A question or the cell's start says nothing of the word.
    private static bool OpensSentence(ReadOnlySpan<char> text, int at) =>
        text[..at].TrimEnd(Openers) is var before && (before.IsEmpty || before[^1] is '.' or '!' or '?' or '…' or ':');

    // A hyphenated word, and its composite parts get taken on their own.
    internal static IEnumerable<string> WithHalves(string word) =>
        word.Contains('-') ? word.Split('-', StringSplitOptions.RemoveEmptyEntries).Prepend(word) : [word];

    internal static bool IsWordChar(char c) => char.IsLetter(c) || c is '\'' or '’' or '-';

    // All capitals probably means one of the game's labels (VOICEMAN, BATTLETALK)
    // A letter three times running is a shout? ("Gwaaagh")
    // Camel case is an identifier, as in "AchievementInfo"
    private static bool IsWorthLearning(ReadOnlySpan<char> word)
    {
        if (word.Length < ShortestWord || NeverLearn.Contains(word))
            return false;

        var lower = false;

        for (var i = 0; i < word.Length; i++)
        {
            var c = word[i];

            if (!IsWordChar(c))
                return false;

            lower |= char.IsLower(c);

            if (i > 0 && char.IsUpper(c) && char.IsLower(word[i - 1]))
                return false;

            if (i > 1 && char.ToLowerInvariant(c) == char.ToLowerInvariant(word[i - 1]) && char.ToLowerInvariant(c) == char.ToLowerInvariant(word[i - 2]))
                return false;
        }

        return lower;
    }

    // FFXIV has "Teh" 46 times in the context of a Yok Huy name, and one "acheive".
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> NeverLearn = new HashSet<string>(
        ["teh", "hte", "adn", "taht", "thier", "recieve", "acheive", "ive", "dont", "youre", "thats", "alot"],
        StringComparer.OrdinalIgnoreCase).GetAlternateLookup<ReadOnlySpan<char>>();

    private static readonly SearchValues<char> WordBreaks = SearchValues.Create(
    [
        ' ', '\t', '\n', '\r', '/', '\\', '(', ')', '[', ']', '{', '}', ',', ':', ';', '"', '<', '>', '.', '!', '?',
        '—', '–', '…', '*', '=', '+', '|', '~', '#', '%', '&', '_',
    ]);

    private static readonly char[] PathMarks = ['/', '\\', '_'];

    private static readonly char[] Trimmed = ['\'', '’', '‘', '-', '“', '”'];

    private static readonly char[] Stops = ['.', '!', '?', '…'];

    private static readonly char[] Openers = [' ', '\t', '\n', '\r', '"', '“', '‘', '\'', '(', '[', '—', '–', '-', '*'];
}
