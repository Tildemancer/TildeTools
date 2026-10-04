using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using static TildeTools.Ui.Widgets;

namespace TildeTools.Modules.Spelling;

internal sealed partial class SpellingModule(SpellingSettings settings, Action save, WindowSystem windows) : IModule
{
    // Null while off
    internal SpellIpc? Ipc { get; private set; }

    private readonly DefineWindow _define = new(settings, LexiconFolder);

    public string Id => "spelling";

    public string Name => "Spelling";

    public string Description => "Underlines misspellings in the chat box, Chat 2 and Instant Messenger, with corrections on " +
                                 "right-click. It knows the game's words, and the people you talk to! It's less creepy than it " +
                                 "sounds, I promise.";

    public bool IsEnabled { get; private set; }

    private static string Dictionaries => Path.Combine(Svc.Pi.AssemblyLocation.DirectoryName!, "Dictionaries");

    private static string LexiconFolder => Path.Combine(Svc.Pi.AssemblyLocation.DirectoryName!, "Lexicon");

    public void Enable()
    {
        IsEnabled = true;
        ImportWordsmith();

        _ = Load();
        Ipc = new SpellIpc(settings, save, LexiconFolder, _define.Open, _define.OpenSearch);

        if (!windows.Windows.Contains(_define))
            windows.AddWindow(_define);
        GameVocabulary.Supply();

        if (settings.LearnPlayerNames)
            StartLearning();
    }

    private async Task Load()
    {
        try
        {
            var terms = Lexicon.Terms(LexiconFolder).SelectMany(term => term.Spellings);

            if (await Speller.Load(Dictionaries, settings.British, settings.CustomWords, terms) is { } loaded)
                Svc.Log.Info(loaded);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"Could not load the dictionaries from {Dictionaries}.");
        }
    }

    public void Disable()
    {
        PlayerVocabulary.Stop();
        GameVocabulary.Forget();
        _define.IsOpen = false;
        Ipc?.Dispose();
        Ipc = null;
        Speller.Unload();
        IsEnabled = false;
    }

    private void ImportWordsmith()
    {
        if (settings.ImportedWordsmith)
            return;

        settings.ImportedWordsmith = true;

        try
        {
            var path = ShippedSetup.PathOf("Wordsmith.json");
            if (!File.Exists(path))
                return;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            // A WS entry CAN be several words. This is mostly to guard against hand edited configs, even if the case isn't likely.
            if (root.TryGetProperty("CustomDictionaryEntries", out var words))
                foreach (var word in words.EnumerateArray().Select(w => w.GetString()).OfType<string>().SelectMany(w => w.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
                    if (!settings.CustomWords.Contains(word, StringComparer.OrdinalIgnoreCase))
                        settings.CustomWords.Add(word);

            if (root.TryGetProperty("DictionaryFile", out var file))
                settings.British = BritishRegex().IsMatch(file.GetString() ?? "");

            // WS's 0 is unlimited, so this translates into our highest legal amount.
            if (root.TryGetProperty("MaximumSuggestions", out var most) && most.TryGetInt32(out var count))
                settings.MaximumSuggestions = count > 0 ? count : SpellingSettings.MostSuggestions;

            if (root.TryGetProperty("IgnoreWordsEndingInHyphen", out var hyphen) && hyphen.ValueKind is JsonValueKind.True or JsonValueKind.False)
                settings.IgnoreWordsEndingInHyphen = hyphen.GetBoolean();

            Svc.Log.Info($"Took {settings.CustomWords.Count} added words and Wordsmith's spelling choices from {path}.");
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "Could not read Wordsmith's spelling settings, so Spelling starts from its own.");
        }
        finally
        {
            save();
        }
    }

    // WS names its dictionary by file so this lets us turn on British spelling by naming 'en_GB.dic'.
    [GeneratedRegex(@"(?i)\b(?:gb|uk|british|en[_-]?gb)\b")]
    private static partial Regex BritishRegex();

    private void StartLearning()
    {
        ApplySettings();
        PlayerVocabulary.Start();
    }

    private void ApplySettings()
    {
        PlayerVocabulary.Suggest = settings.SuggestPlayerNames;
        PlayerVocabulary.TellPartnerDays = settings.TellPartnerDays;
        PlayerVocabulary.RequestCompanyRoster = settings.RequestCompanyRoster;
    }

    private static readonly string RosterNote =
        "Risk management: when on, it asks when you log in, and when someone in your Free Company logs in or " +
        "out. It never asks with the FC window open, in combat, or more than once every " +
        $"{PlayerVocabulary.RosterCooldown.TotalMinutes} minutes.";

    private int _suggestions = -1;
    private int _days = -1;

    public void DrawTab()
    {
        using var wrap = ImRaii.TextWrapPos(0f);

        ImGui.TextUnformatted("Dictionary");

        if (Toggle("British spelling first", settings.British, settings, static (s, v) => s.British = v))
        {
            save();
            _ = Load();
        }

        ImGui.TextDisabled("Both are always accepted: this picks whose corrections come first.");

        if (Paced("Corrections to offer", ref _suggestions, settings.MaximumSuggestions, 1, SpellingSettings.MostSuggestions, settings, static (s, v) => s.MaximumSuggestions = v))
            save();

        if (Toggle("Skip a word cut off with a hyphen", settings.IgnoreWordsEndingInHyphen, settings, static (s, v) => s.IgnoreWordsEndingInHyphen = v))
            save();

        ImGui.TextDisabled("\"I was going to-\" isn't an actual typo.");

        ImGui.Separator();
        ImGui.TextUnformatted("Define and synonyms");
        ImGui.TextDisabled("Right-click any word in the chat box, Chat 2 or Instant Messenger. Also try it in the preview window of Chat 2!");

        if (Toggle("Look a word up online when the bundled definitions lack it", settings.LookUpOnline, settings, static (s, v) => s.LookUpOnline = v))
            save();

        ImGui.TextDisabled("Define can make a network call to Wiktionary, if it can't define a word locally.");

        ImGui.Separator();
        DrawOwnWords();

        ImGui.Separator();
        DrawPlayerNames();
    }

    private void DrawOwnWords()
    {
        ImGui.TextUnformatted($"Your words ({settings.CustomWords.Count})");
        ImGui.TextDisabled("Words you've added to your dictionary.");

        using var list = ImRaii.Child("##spelling-words", new(0, ImGui.GetTextLineHeightWithSpacing() * Math.Min(8, settings.CustomWords.Count + 1)), true);
        if (!list.Success)
            return;

        // Only the rows in view! Long word lists and short word lists are identical in performance cost.
        // I love performance
        ImGuiClip.ClippedDraw(settings.CustomWords, _drawWord ??= DrawWord, 1, ImGui.GetTextLineHeightWithSpacing());

        if (_removed is not { } removed)
            return;

        _removed = null;
        _ = settings.CustomWords.Remove(removed);
        Speller.RemoveWord(removed);
        save();
    }

    private Action<string>? _drawWord;
    private string? _removed;

    // Kept in _drawWord, a lambda in DrawOwnWords would be remade every frame along with its capture.
    private void DrawWord(string word)
    {
        if (ImGui.SmallButton($"x##{word}"))
            _removed = word;

        ImGui.SameLine();
        ImGui.TextUnformatted(word);
    }

    private void DrawPlayerNames()
    {
        ImGui.TextUnformatted("People's names");
        ImGui.TextDisabled("Friends, your free company, recent tell partners, your party, and anyone who's said your name this session.");

        if (Toggle("Stop marking their names as misspelled", settings.LearnPlayerNames, settings, static (s, v) => s.LearnPlayerNames = v))
        {
            if (settings.LearnPlayerNames)
                StartLearning();
            else
                PlayerVocabulary.Stop();

            save();
        }

        if (!settings.LearnPlayerNames)
            return;

        var changed = Toggle("Finish the names of friends, your free company and tell partners as you type them", settings.SuggestPlayerNames, settings, static (s, v) => s.SuggestPlayerNames = v);

        ImGui.TextDisabled("Offers \"Emmanellain\" once you've typed \"Emmanel\". Your party and anyone else are only ever accepted, not suggested.");

        changed |= Paced("Days of tells to count", ref _days, settings.TellPartnerDays, 1, SpellingSettings.MostTellPartnerDays, settings, static (s, v) => s.TellPartnerDays = v);

        ImGui.TextDisabled("Tells older than this don't count. Friends and free company always do.");

        changed |= Toggle("DANGER! Fetch the free company roster without being asked", settings.RequestCompanyRoster, settings, static (s, v) => s.RequestCompanyRoster = v);

        ImGui.TextDisabled("Asks for the roster the way opening the Free Company window does. This is an automated request " +
                           "that, while theoretically identical to you opening it yourself, might be detectable Square-side -- " +
                           "this is a dangerous permission to check! You tick it at your own risk.");
        ImGui.TextDisabled(RosterNote);

        if (changed)
        {
            ApplySettings();
            PlayerVocabulary.Invalidate(now: true);
            save();
        }

        ImGui.Spacing();
        ImGui.TextDisabled(PlayerVocabulary.Summary());
    }

    public void Dispose()
    {
        if (IsEnabled)
            Disable();
    }
}
