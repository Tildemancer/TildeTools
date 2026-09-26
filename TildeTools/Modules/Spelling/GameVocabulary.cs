using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TildeTools.Modules.Spelling;

internal static class GameVocabulary
{
    private static int _generation;

    // Before the unload, so a gather still running stops at its next batch
    internal static void Forget() => Interlocked.Increment(ref _generation);

    // Not checked against IsWord: one passing only as a name or added word would stop when that went
    internal static void Supply()
    {
        var generation = _generation;

        _ = Task.Run(() =>
        {
            try
            {
                var clock = Stopwatch.StartNew();
                var words = GameText.Words(Svc.Data.GameData).ToList();

                // Checked per batch, not held across them: Forget runs on the framework thread
                if (!Speller.AddGameWords(words, () => generation == Volatile.Read(ref _generation)))
                    return;

                Svc.Log.Info($"Taught the spellchecker {words.Count} words from the game's text in {clock.Elapsed.TotalSeconds:F1} s.");
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "Could not read the game's words for the spellchecker.");
            }
        });
    }
}
