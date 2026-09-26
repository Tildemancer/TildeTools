using System.Collections.Generic;

namespace TildeTools.Modules.EmoteSplitter.Splitting;

public sealed class SplitOptions
{
    // Whole line: header, OOC, markers
    // 500 is the chat box's MaxByte
    public int MaxBytes { get; set; } = 500;

    public int SafetyMargin { get; set; }

    public bool PreferSentenceBreaks { get; set; } = true;

    public string ContinuationPrefix { get; set; } = string.Empty;

    public string ContinuationSuffix { get; set; } = string.Empty;

    public string FinalMarker { get; set; } = string.Empty;

    public bool MarkersOnSingleChunk { get; set; }

    public List<ChunkMarker> Markers { get; set; } = [];

    public bool IsOoc { get; set; }

    // Just the tag, the splitter adds the space
    public string OocOpen { get; set; } = string.Empty;

    public string OocClose { get; set; } = string.Empty;
}
