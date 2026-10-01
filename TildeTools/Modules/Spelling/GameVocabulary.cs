using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TildeTools.Modules.Spelling;

internal static class GameVocabulary
{
    private static readonly Lock _sync = new();
    private static int _generation;

    // For the Define window!
    // null till read
    private static Dictionary<string, GameName>? _names;

    internal static bool Ready => _names is not null;

    // Called before the unload, so that any gather still running stops at its next batch.
    internal static void Forget()
    {
        lock (_sync)
        {
            Interlocked.Increment(ref _generation);
            _names = null;
        }
    }

    // "Alphinaud's" is Alphinaud, though the game technically prints the possessive too.
    internal static GameName? NameOf(string word) =>
        _names is { } names && (names.TryGetValue(Lexicon.Stem(word), out var name) || names.TryGetValue(word, out name)) ? name : null;

    // Not checked against IsWord, otherwise a word passing only as a name or added word would stop passing when that went.
    internal static void Supply()
    {
        var generation = _generation;

        _ = Task.Run(() =>
        {
            try
            {
                var clock = Stopwatch.StartNew();
                var (words, names) = GameText.Read(Svc.Data.GameData);

                // Checked per batch rather than held across them, because Forget runs on the framework thread.
                if (!Speller.AddGameWords(words, names, () => generation == Volatile.Read(ref _generation)))
                    return;

                lock (_sync)
                    if (generation == _generation)
                        _names = names;

                Svc.Log.Info($"Taught the spellchecker {words.Count} words from the game's text, {words.Count(names.ContainsKey)} of them names, in {clock.Elapsed.TotalSeconds:F1} s.");
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "Could not read the game's words for the spellchecker.");
            }
        });
    }
}
