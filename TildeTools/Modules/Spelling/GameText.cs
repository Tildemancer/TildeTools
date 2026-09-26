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
using Seen = System.Collections.Generic.Dictionary<string, (int Count, int Shortest)>.AlternateLookup<System.ReadOnlySpan<char>>;

namespace TildeTools.Modules.Spelling;

internal static class GameText
{
    // "gil" is three
    private const int ShortestWord = 3;

    // In words: "Plate Pauldron" is two
    private const int NameSized = 4;

    // Not through ExcelModule, which keeps every sheet it opens: 255 MB for all, measured
    // Lumina's file cache holds weak references only
    internal static IEnumerable<string> Words(GameData data)
    {
        Dictionary<string, (int Count, int Shortest)> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (var name in data.Excel.SheetNames)
            TakeSheet(data, name, seen.GetAlternateLookup<ReadOnlySpan<char>>());

        // A one-off in prose is slurring or a typo: "latesht", "acheive"
        return seen.Where(p => p.Value.Count >= 2 || p.Value.Shortest <= NameSized).Select(p => p.Key);
    }

    private static void TakeSheet(GameData data, string name, Seen seen)
    {
        if (data.GetFile<ExcelHeaderFile>($"exd/{name}.exh") is not { } header)
            return;

        // As ExcelModule.GetSheet picks
        int[] columns = [.. header.ColumnDefinitions.Where(c => c.Type == ExcelColumnDataType.String).Select(c => (int)c.Offset)];
        var suffix = header.Languages.Contains(Language.English) ? "_en" : header.Languages.Contains(Language.None) ? "" : null;
        if (columns.Length == 0 || suffix is null)
            return;

        // A row: a 6-byte header, then DataOffset bytes (per subrow, after a 2-byte id), then the strings those bytes point into
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
                    foreach (var column in columns)
                    {
                        var text = bytes.AsSpan(strings + (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 6 + i * stride + id + column)));
                        var cell = new ReadOnlySeStringSpan(text[..text.IndexOf((byte)0)]);

                        // "_rsv_" cells are sent by the server, and Dalamud's RsvResolver has those sent so far
                        Take(seen, cell.IsRsv() && data.Options.RsvResolver is { } resolve && resolve(new ReadOnlySeString(cell.Data), out var sent)
                            ? sent.ExtractText()
                            : cell.ExtractText());
                    }
                }
            }
        }
    }

    // On spans, so only a word not seen before becomes a string: splitting made 1.1 GB of garbage a load
    private static void Take(Seen seen, ReadOnlySpan<char> text)
    {
        if (!text.Contains(' ') && text.IndexOfAny(PathMarks) >= 0)
            return;

        var tokens = 0;
        foreach (var range in text.SplitAny(WordBreaks))
            tokens += text[range].IsEmpty ? 0 : 1;

        foreach (var range in text.SplitAny(WordBreaks))
        {
            var token = text[range].Trim(Trimmed);

            // As WithHalves
            Learn(token);

            if (token.Contains('-'))
                foreach (var half in token.Split('-'))
                    Learn(token[half]);
        }

        void Learn(ReadOnlySpan<char> word)
        {
            if (!IsWorthLearning(word))
                return;

            ref var was = ref CollectionsMarshal.GetValueRefOrAddDefault(seen, word, out var known);
            was = (was.Count + 1, known ? Math.Min(was.Shortest, tokens) : tokens);
        }
    }

    // A hyphenated word, and its halves on their own
    internal static IEnumerable<string> WithHalves(string word) =>
        word.Contains('-') ? word.Split('-', StringSplitOptions.RemoveEmptyEntries).Prepend(word) : [word];

    internal static bool IsWordChar(char c) => char.IsLetter(c) || c is '\'' or '’' or '-';

    // All capitals are the game's labels: VOICEMAN, BATTLETALK
    // A letter three times running is a shout: "Gwaaagh"
    // A capital after lowercase is an identifier: "AchievementInfo"
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

    // The game has "Teh" 46 times, a Yok Huy name, and one "acheive"
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
}
