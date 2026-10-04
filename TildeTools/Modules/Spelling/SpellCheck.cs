using System;
using System.Collections.Generic;
using System.Linq;

namespace TildeTools.Modules.Spelling;

internal static class SpellCheck
{
    internal static List<(int Index, int Length)> Misspellings(string text, bool skipHyphenEnded)
    {
        List<(int Index, int Length)> found = [];

        for (var i = 0; i < text.Length;)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;

            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
                i++;

            if (i > start && Misspelled(text, start, i, skipHyphenEnded) is { } span)
                found.Add(span);
        }

        return found;
    }

    // Unlike Misspellings, slashes and dashes part words here, as in "heavy/large" or "wait—what".
    internal static (int Index, int Length)? WordAt(string text, int index)
    {
        if (index < 0 || index >= text.Length || Parts(text[index]))
            return null;

        var (start, end) = (index, index + 1);

        while (start > 0 && !Parts(text[start - 1]))
            start--;

        while (end < text.Length && !Parts(text[end]))
            end++;

        return Trimmed(text, start, end);

        static bool Parts(char c) => char.IsWhiteSpace(c) || c is '/' or '\\' or '—' or '–';
    }

    // The part of WordAt's word under index, or the one after the hyphen it's on, as in "river-wood".
    internal static (int Index, int Length)? HalfAt(string text, int start, int length, int index)
    {
        index = Math.Clamp(index, start, start + length - 1);
        if (text[index] == '-')
            index++;

        var (from, to) = (Math.Max(start, text.LastIndexOf('-', index) + 1), text.IndexOf('-', index, start + length - index));
        return Trimmed(text, from, to < 0 ? start + length : Math.Max(from, to));
    }

    internal static (int Index, int Length)? Trimmed(string text, int start, int end)
    {
        while (start < end && !char.IsLetterOrDigit(text[start]))
            start++;

        while (end > start && !char.IsLetterOrDigit(text[end - 1]))
            end--;

        return start == end ? null : (start, end - start);
    }

    private static (int Index, int Length)? Misspelled(string text, int start, int end, bool skipHyphenEnded)
    {
        // The mark right after the word, so that "goi-," is cut off too.
        if (Trimmed(text, start, end) is not var (index, length) || skipHyphenEnded && index + length < end && text[index + length] == '-')
            return null;

        // A slash right before the word makes it a command, which isn't checked
        if (index > start && text[index - 1] == '/')
            return null;

        var word = text.Substring(index, length);

        // Trimmed drops the dot "e.g." is listed with. I.e, "e.g.". hehehe.
        // im going insane
        if (word.AsSpan().ContainsAnyInRange('0', '9') || word.Contains("://") || word.StartsWith("www.", StringComparison.OrdinalIgnoreCase) || word.AsSpan().IndexOfAnyExcept(RomanDigits) < 0 || Accepted(word)
            || index + length < end && text[index + length] == '.' && Speller.IsWord(word + "."))
            return null;

        return word.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries) is { Length: > 1 } parts && parts.All(Accepted)
            ? null
            : (index, length);
    }

    private static bool Accepted(string word)
    {
        if (Speller.IsWord(word))
            return true;

        foreach (var ending in Endings)
            if (word.Length > ending.Length && word.EndsWith(ending, StringComparison.OrdinalIgnoreCase))
                return Speller.IsWord(word[..^ending.Length]);

        return false;
    }

    private static readonly string[] Endings = ["'s", "’s", "'ll", "’ll", "'d", "’d", "'m", "’m", "'em", "’em", "'re", "’re", "'ve", "’ve"];

    private const string RomanDigits = "IVXLCDM";
}
