namespace TildeTools.Modules.EmoteSplitter;

// Crosses IPC as an int, don't renumber: Chat 2 and Messenger read the numbers
public enum SplitTake
{
    // Caller sends it itself
    NotTaken = 0,

    // Caller sends nothing, may clear its box
    Queued = 1,

    // Caller sends nothing, keeps the text
    Refused = 2,
}
