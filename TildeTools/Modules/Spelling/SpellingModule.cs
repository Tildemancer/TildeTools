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

    public string Description => "Underlines misspellings in the chat box, Chat 2 and Instant Messenger, with corrections on right-click. It knows the game's words and the people you talk to.";

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

            // A Wordsmith entry can be several words
            if (root.TryGetProperty("CustomDictionaryEntries", out var words))
                foreach (var word in words.EnumerateArray().Select(w => w.GetString()).OfType<string>().SelectMany(w => w.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
                    if (!settings.CustomWords.Contains(word, StringComparer.OrdinalIgnoreCase))
                        settings.CustomWords.Add(word);

            if (root.TryGetProperty("DictionaryFile", out var file))
                settings.British = BritishRegex().IsMatch(file.GetString() ?? "");

            // Wordsmith's 0 is unlimited
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

    // My Wordsmith fork's test, gone in e5977a6
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
        PlayerVocabulary.Invalidate(now: true);
    }

    private void ApplyAndSave()
    {
        ApplySettings();
        save();
    }

    private static readonly string RosterNote =
        $"Asked when this is turned on, when you log in, and when someone in your free company logs in or out. Never with that window open, in combat, or more than once every {PlayerVocabulary.RosterCooldown.TotalMinutes} minutes.";

    private int _suggestions = -1;
    private int _days = -1;

    public void DrawTab()
    {
        using var wrap = ImRaii.TextWrapPos(0f);

        ImGui.TextUnformatted("Dictionary");

        if (Toggle("British spelling first", settings.British, v => settings.British = v))
        {
            save();
            _ = Load();
        }

        ImGui.TextDisabled("Both are always accepted: this picks whose corrections come first.");

        if (Paced("Corrections to offer", ref _suggestions, settings.MaximumSuggestions, 1, SpellingSettings.MostSuggestions, v => settings.MaximumSuggestions = v))
            save();

        if (Toggle("Skip a word cut off with a hyphen", settings.IgnoreWordsEndingInHyphen, v => settings.IgnoreWordsEndingInHyphen = v))
            save();

        ImGui.TextDisabled("\"I was going to-\" isn't a typo.");

        ImGui.Separator();
        ImGui.TextUnformatted("Define and synonyms");
        ImGui.TextDisabled("Right-click any word in the chat box, Chat 2 or Instant Messenger.");

        if (Toggle("Look a word up online when the bundled definitions lack it", settings.LookUpOnline, v => settings.LookUpOnline = v))
            save();

        ImGui.TextDisabled("Define asks Wiktionary for that one word, and only then.");

        ImGui.Separator();
        DrawOwnWords();

        ImGui.Separator();
        DrawPlayerNames();
    }

    private void DrawOwnWords()
    {
        ImGui.TextUnformatted($"Your words ({settings.CustomWords.Count})");
        ImGui.TextDisabled("Added with \"Add to dictionary\" on a right-click. Remove one here to have it marked again.");

        using var list = ImRaii.Child("##spelling-words", new(0, ImGui.GetTextLineHeightWithSpacing() * Math.Min(8, settings.CustomWords.Count + 1)), true);
        if (!list.Success)
            return;

        string? removed = null;

        // Only the rows in view
        ImGuiClip.ClippedDraw(settings.CustomWords, word =>
        {
            using var id = ImRaii.PushId(word);

            if (ImGui.SmallButton("x"))
                removed = word;

            ImGui.SameLine();
            ImGui.TextUnformatted(word);
        }, 1, ImGui.GetTextLineHeightWithSpacing());

        if (removed is null)
            return;

        _ = settings.CustomWords.Remove(removed);
        Speller.RemoveWord(removed);
        save();
    }

    private void DrawPlayerNames()
    {
        ImGui.TextUnformatted("People's names");
        ImGui.TextDisabled("Friends, your free company, recent tell partners, your party, and anyone who's said your name this session.");

        if (Toggle("Stop marking their names as misspelled", settings.LearnPlayerNames, v => settings.LearnPlayerNames = v))
        {
            if (settings.LearnPlayerNames)
                StartLearning();
            else
                PlayerVocabulary.Stop();

            save();
        }

        if (!settings.LearnPlayerNames)
            return;

        if (Toggle("Offer the names of friends, your free company and tell partners as corrections", settings.SuggestPlayerNames, v => settings.SuggestPlayerNames = v))
            ApplyAndSave();

        ImGui.TextDisabled("Suggests \"Emmanellain\" for \"Emmanelain\". Your party and anyone else are only ever accepted.");

        if (Paced("Days of tells to count", ref _days, settings.TellPartnerDays, 1, SpellingSettings.MostTellPartnerDays, v => settings.TellPartnerDays = v))
            ApplyAndSave();

        ImGui.TextDisabled("Tells older than this don't count. Friends and free company always do.");

        if (Toggle("Fetch the free company roster without being asked", settings.RequestCompanyRoster, v => settings.RequestCompanyRoster = v))
            ApplyAndSave();

        ImGui.TextDisabled("Asks for the roster the way opening the Free Company window does.");
        ImGui.TextDisabled(RosterNote);

        ImGui.Spacing();
        ImGui.TextDisabled(PlayerVocabulary.Summary());
    }

    public void Dispose()
    {
        if (IsEnabled)
            Disable();
    }
}
