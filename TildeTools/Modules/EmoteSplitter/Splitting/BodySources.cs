using System;
using System.Collections.Generic;

namespace TildeTools.Modules.EmoteSplitter.Splitting;

// Where each part's body sits in the typed line
// Headers, markers and trimming move a part's own offsets off the clicked word
public static class BodySources
{
    // Searched, not summed: trimming and the OOC lift shift things unpredictably
    // Empty when any body can't be found
    // `from` skips the channel command, or "/say say say" matches inside the header
    // Never partly over a break marker's text, or the body "3" in "|n3 3" is found in the marker
    // Wholly is fine: "and|nor" is a body's own text
    public static IReadOnlyList<int> Locate(string source, IReadOnlyList<SplitPart> parts, int from = 0)
    {
        var marks = new List<(int At, int Length)>();
        for (var mark = MessageSplitter.FindBreak(source, from, anywhere: true); mark.At >= 0;
             mark = MessageSplitter.FindBreak(source, mark.At + 1, anywhere: true))
            marks.Add((mark.At, mark.Length));

        var starts = new List<int>(parts.Count);
        var pos = from;

        foreach (var part in parts)
        {
            var body = part.Line.Substring(part.BodyStart, part.BodyLength);

            var at = source.IndexOf(body, pos, StringComparison.Ordinal);
            while (at >= 0 && Cuts(marks, at, body.Length))
                at = source.IndexOf(body, at + 1, StringComparison.Ordinal);

            if (at < 0)
                return [];

            starts.Add(at);
            pos = at + body.Length;
        }

        return starts;
    }

    private static bool Cuts(List<(int At, int Length)> marks, int at, int length)
    {
        foreach (var mark in marks)
            if (mark.At < at + length && at < mark.At + mark.Length && (mark.At < at || mark.At + mark.Length > at + length))
                return true;

        return false;
    }
}
