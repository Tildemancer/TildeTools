using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Dalamud.Configuration;
using TildeTools.Modules;
using TildeTools.Modules.ChatTwo;
using TildeTools.Modules.EmoteSplitter;
using TildeTools.Modules.Spelling;
using TildeTools.Modules.Wordsmith;

namespace TildeTools;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    public Dictionary<string, bool> EnabledModules { get; set; } = [];

    public EmoteSplitterSettings EmoteSplitter { get; set; } = new();

    public ChatTwoSettings ChatTwo { get; set; } = new();

    public SpellingSettings Spelling { get; set; } = new();

    public WordsmithSettings? Wordsmith { get; set; }

    public bool SetupSeen { get; set; }

    public HashSet<string> DeclinedSetups { get; set; } = [];

    public bool IsModuleEnabled(string id) => EnabledModules.GetValueOrDefault(id, true);

    public void SetModuleEnabled(string id, bool enabled) => EnabledModules[id] = enabled;

    internal bool FirstRun { get; private set; }

    internal static Configuration Load()
    {
        if (Svc.Pi.GetPluginConfig() is not Configuration config)
        {
            config = new Configuration { FirstRun = true };

            if (LegacySettings() is { } legacy)
            {
                config.EmoteSplitter = legacy;
                Svc.Log.Info("Imported settings from the previous standalone Emote Splitter plugin.");
            }
        }

        config.EmoteSplitter.Migrate();
        config.MoveNameSettings();

        // 2 made the roster fetch off by default, for setups saved before it too
        if (config.Version < 2)
            (config.Spelling.RequestCompanyRoster, config.Version) = (false, 2);

        return config;
    }

    private void MoveNameSettings()
    {
        if (Wordsmith is not { } old)
            return;

        (Spelling.LearnPlayerNames, Spelling.SuggestPlayerNames, Spelling.TellPartnerDays) =
            (old.LearnPlayerNames, old.SuggestPlayerNames, old.TellPartnerDays);

        // Up to 0.4.5, Spelling came with Wordsmith
        if (EnabledModules.TryGetValue("wordsmith", out var wordsmith))
            EnabledModules.TryAdd("spelling", wordsmith);

        Wordsmith = null;
    }

    private static EmoteSplitterSettings? LegacySettings()
    {
        try
        {
            var path = ShippedSetup.PathOf("EmoteSplitterXIV.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<EmoteSplitterSettings>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "Could not read the old Emote Splitter settings; starting fresh.");
            return null;
        }
    }
}
