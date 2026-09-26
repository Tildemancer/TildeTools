using System;
using System.Collections.Generic;

using TildeTools.Modules.EmoteSplitter.Sending;
using TildeTools.Modules.EmoteSplitter.Splitting;

namespace TildeTools.Modules.EmoteSplitter;

// Legacy, only for migrating old settings
public enum MarkerPlacement
{
    End,
    Start,
}

[Serializable]
public sealed class EmoteSplitterSettings
{
    // Text positions are 16-bit signed and the game adds offsets to lengths, so stay under 32767
    public const int MaxUnlockBytes = 32000;

    public const int MinUnlockBytes = 512;

    // Text still fits past the largest margin and a splitting "/r ", which holds back room for the longest /tell
    public const int MinChunkBytes = MaxSafetyMarginBytes + 3 + ReplyPin.HeaderAllowance + MinTextBytes;

    // The chat box's own limit
    // At 512, a line of 501 to 512 bytes gets past the submit hook unsplit
    public const int MaxChunkBytes = 500;

    public const int MaxSafetyMarginBytes = 64;

    public const int MinChunksPerMessage = 2;
    public const int MaxChunksPerMessageCeiling = 60;

    public int Version { get; set; } = 1;

    public bool UnlockChatInput { get; set; } = true;

    // Clamped in the setters too: a hand-edited config skips the UI
    public int UnlockedMaxBytes { get; set => field = Math.Clamp(value, MinUnlockBytes, MaxUnlockBytes); } = 8000;

    public int MaxBytesPerChunk { get; set => field = Math.Clamp(value, MinChunkBytes, MaxChunkBytes); } = MaxChunkBytes;

    public int SafetyMargin { get; set => field = Math.Clamp(value, 0, MaxSafetyMarginBytes); }

    internal int Budget => MaxBytesPerChunk - SafetyMargin;

    public const int MinTextBytes = 16;

    public bool PreferSentenceBreaks { get; set; } = true;

    public string ContinuationPrefix { get; set; } = string.Empty;

    public string ContinuationSuffix { get; set; } = string.Empty;

    public string FinalMarker { get; set; } = string.Empty;

    public bool MarkersOnSingleChunk { get; set; }

    public List<ChunkMarker> Markers { get; set; } = [];

    public bool WrapOocPerPart { get; set; } = true;

    public string OocOpen { get; set; } = "((";

    public string OocClose { get; set; } = "))";

    // Superseded, only Migrate reads these two
    public string ContinuationMarker { get; set; } = string.Empty;
    public MarkerPlacement MarkerPlacement { get; set; } = MarkerPlacement.End;

    // A macro's free company lines landed 166.7 ms apart, ten frames at 60 fps (measured 2026-09-24)
    public const int MacroPaceMs = 167;

    public const int DefaultIntervalMs = 1500;

    public int IntervalMs { get; set => field = Math.Clamp(value, SendQueue.MinIntervalMs, SendQueue.MaxIntervalMs); } = DefaultIntervalMs;

    public int FreeIntervalMs { get; set => field = Math.Clamp(value, 0, SendQueue.MaxIntervalMs); } = MacroPaceMs;

    // Stops a stray paste becoming an hour of chat
    // A ceiling too, or the guard could be set out of reach
    public int MaxChunksPerMessage { get; set => field = Math.Clamp(value, MinChunksPerMessage, MaxChunksPerMessageCeiling); } = 20;

    public bool RetryOnThrottle { get; set; } = true;

    public bool ShowProgress { get; set; } = true;

    public SplitOptions ToSplitOptions() => new()
    {
        MaxBytes = MaxBytesPerChunk,
        SafetyMargin = SafetyMargin,
        PreferSentenceBreaks = PreferSentenceBreaks,
        ContinuationPrefix = ContinuationPrefix,
        ContinuationSuffix = ContinuationSuffix,
        FinalMarker = FinalMarker,
        MarkersOnSingleChunk = MarkersOnSingleChunk,
        Markers = Markers,
    };

    public string DetachOoc(string body, SplitOptions options)
    {
        if (!WrapOocPerPart || OocOpen.Length == 0 || OocClose.Length == 0)
            return body;

        var trimmed = body.Trim();
        if (trimmed.Length <= OocOpen.Length + OocClose.Length)
            return body;

        if (!trimmed.StartsWith(OocOpen, StringComparison.Ordinal) ||
            !trimmed.EndsWith(OocClose, StringComparison.Ordinal))
            return body;

        // Only one block: lifting "(( a )) |n (( b ))" gives "(( a )) ))" and "(( (( b ))"
        var inner = trimmed[OocOpen.Length..^OocClose.Length];
        if (inner.Contains(OocClose, StringComparison.Ordinal))
            return body;

        options.IsOoc = true;
        options.OocOpen = OocOpen;
        options.OocClose = OocClose;

        return inner.Trim();
    }

    public void Migrate()
    {
        if (Version < 2 && ContinuationMarker.Length > 0)
        {
            if (MarkerPlacement == MarkerPlacement.Start)
                ContinuationPrefix = ContinuationMarker;
            else
                ContinuationSuffix = ContinuationMarker;
        }

        // 0 was 0.4.5's test value, parts a frame apart
        if (Version < 3 && FreeIntervalMs == 0)
            FreeIntervalMs = MacroPaceMs;

        Version = 3;
    }
}
