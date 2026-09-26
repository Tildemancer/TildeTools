using System;
using System.Collections.Generic;

namespace TildeTools.Modules.Spelling;

[Serializable]
public sealed class SpellingSettings
{
    public bool British { get; set; }

    public const int MostSuggestions = 10;

    public int MaximumSuggestions { get; set => field = Math.Clamp(value, 1, MostSuggestions); } = 5;

    public bool IgnoreWordsEndingInHyphen { get; set; } = true;

    public List<string> CustomWords { get; set; } = [];

    public bool LookUpOnline { get; set; } = true;

    public bool LearnPlayerNames { get; set; } = true;

    public bool SuggestPlayerNames { get; set; } = true;

    public const int MostTellPartnerDays = 365;

    public int TellPartnerDays { get; set => field = Math.Clamp(value, 1, MostTellPartnerDays); } = 30;

    // A real network request made on your behalf. DANGER!
    // Todo: CRITICAL >>>>>>> THIS WILL NOT PASS DALAMUD PAC, REMOVE THIS BEFORE YOU EVEN THINK ABOUT MAIN REPO! <<<<<<<<<<
    public bool RequestCompanyRoster { get; set; }

    public bool ImportedWordsmith { get; set; }
}
