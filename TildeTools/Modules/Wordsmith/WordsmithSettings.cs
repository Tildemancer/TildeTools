using System;

namespace TildeTools.Modules.Wordsmith;

// Legacy, see Configuration.MoveNameSettings
// Name and namespace stay, the saved config's $type points here
[Serializable]
public sealed class WordsmithSettings
{
    public bool LearnPlayerNames { get; set; } = true;

    public bool SuggestPlayerNames { get; set; } = true;

    public int TellPartnerDays { get; set; } = 30;
}
