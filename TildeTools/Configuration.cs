using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using TildeTools.Modules.ChatTwo;
using TildeTools.Modules.EmoteSplitter;
using TildeTools.Modules.Spelling;

namespace TildeTools;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    public Dictionary<string, bool> EnabledModules { get; set; } = [];

    public EmoteSplitterSettings EmoteSplitter { get; set; } = new();

    public ChatTwoSettings ChatTwo { get; set; } = new();

    public SpellingSettings Spelling { get; set; } = new();

    public bool SetupSeen { get; set; }

    public HashSet<string> DeclinedSetups { get; set; } = [];

    public bool IsModuleEnabled(string id) => EnabledModules.GetValueOrDefault(id, true);

    public void SetModuleEnabled(string id, bool enabled) => EnabledModules[id] = enabled;

    internal bool FirstRun { get; private set; }

    internal static Configuration Load() => Svc.Pi.GetPluginConfig() as Configuration ?? new Configuration { FirstRun = true };
}
