using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace TildeTools.Modules.Spelling;

// _present is only accepted: often strangers, and rare surnames hijack ordinary typos
internal static class PlayerVocabulary
{
    // FFXIV allows 2 letters, but a name that short can't be told from a real word, so we're making the executive decision to skip it
    private const int ShortestName = 3;

    // Re-reads are event-driven, this just stops a burst of FC logins costing one each
    private const int QuietSeconds = 20;

    private static readonly HashSet<string> _friends = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _company = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _tells = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _present = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object _sync = new();

    private static DateTime _nextRefresh = DateTime.MinValue;
    private static bool _listening;
    private static bool _tellsStale;

    // A new window has to reread the logs, or the change waits for a login
    internal static int TellPartnerDays
    {
        get;
        set
        {
            _tellsStale |= value != field;
            field = value;
        }
    }

    internal static bool Suggest { get; set; }

    // now skips QuietSeconds, for a settings change the player is looking at
    internal static void Invalidate(bool now = false)
    {
        _wanted = true;

        if (now)
            _nextRefresh = DateTime.MinValue;
    }

    private static bool _wanted;

    internal static unsafe string Summary()
    {
        lock (_sync)
        {
            var summary = _friends.Count + _company.Count + _tells.Count + _present.Count + _party.Count == 0
                ? "No names learned yet."
                : $"Names learned: friends {_friends.Count}, free company {_company.Count}, tell partners {_tells.Count}, this session {_present.Count + _party.Count}.";

            // An empty roster looks broken, and when we're not asking only the player can fix it
            if (!_companySeen && TryRoster(out _, out _))
                summary += RequestCompanyRoster
                    ? " Waiting on the free company roster."
                    : " Open your Free Company window once to load its roster.";

            return summary;
        }
    }

    internal static void Start()
    {
        Svc.Chat.ChatMessageHandled += OnChatMessage;
        Svc.Chat.ChatMessageUnhandled += OnChatMessage;
        Svc.ClientState.Login += OnLogin;
        Svc.Framework.Update += OnUpdate;
        _listening = true;

        // The one full read, after this changes come from the chat log
        // On the next Tick, the game's thread: Start can run in the plugin's constructor, off it
        (_wanted, _tellsStale, _nextRefresh) = (true, true, DateTime.MinValue);
    }

    private static void OnLogin()
    {
        Forget();

        // Only a new login skips RosterCooldown, a stop and start of name learning waits it out
        _nextRosterRequest = DateTime.MinValue;
        Reread(rescanTells: true);
    }

    internal static void Stop()
    {
        Svc.Chat.ChatMessageHandled -= OnChatMessage;
        Svc.Chat.ChatMessageUnhandled -= OnChatMessage;
        Svc.ClientState.Login -= OnLogin;
        Svc.Framework.Update -= OnUpdate;
        _listening = false;

        Forget();

        // Caught, so Disable still goes on to GameVocabulary.Forget and Speller.Unload
        lock (_sync)
        {
            try
            {
                Speller.SetPeople([], []);
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "Could not clear the player names from the spellchecker.");
            }
        }
    }

    private static void Forget()
    {
        lock (_sync)
        {
            _friends.Clear();
            _company.Clear();
            _tells.Clear();
            _present.Clear();
            _party.Clear();
            _companySeen = false;
        }

        _rosterStale = true;
    }

    private static double _sinceLook;

    // Every couple of seconds, not every frame
    // Party changes are rare, and the costly sources have their own timer, QuietSeconds
    private static void OnUpdate(IFramework framework)
    {
        _sinceLook += framework.UpdateDelta.TotalSeconds;
        if (_sinceLook < 2)
            return;

        _sinceLook = 0;
        Tick();
    }

    private static void Tick()
    {
        // Every tick, so one skipped for combat or loading goes out once that ends
        MaybeRequestRoster();

        // Until the roster's been read, so opening the FC window, as Summary says to, gets picked up
        var changed = GatherParty() | (!_companySeen && GatherFreeCompany());

        if (_wanted && DateTime.UtcNow >= _nextRefresh)
            Reread(rescanTells: _tellsStale);
        else if (changed)
            Publish();
    }

    private static void Reread(bool rescanTells)
    {
        _wanted = false;
        _tellsStale &= !rescanTells;
        _nextRefresh = DateTime.UtcNow.AddSeconds(QuietSeconds);

        // Framework thread
        GatherFriends();
        GatherFreeCompany();

        if (!rescanTells)
            Publish();
        else
            _ = Task.Run(() =>
            {
                try
                {
                    GatherTellPartners();
                    Publish();
                }
                catch (Exception ex)
                {
                    Svc.Log.Error(ex, "Could not read the tell history for the spellchecker.");
                }
            });
    }

    // Under _sync with Stop's clear, so a tell rescan finishing after it can't put the names back
    private static void Publish()
    {
        lock (_sync)
        {
            if (!_listening)
                return;

            List<string> standing = [.. _friends, .. _company, .. _tells];
            Speller.SetPeople(Suggest ? standing : [], [.. standing, .. _present, .. _party]);
        }
    }

    private static unsafe void GatherFriends()
    {
        var list = InfoProxyFriendList.Instance();
        if (list == null)
            return;

        HashSet<string> found = new(StringComparer.OrdinalIgnoreCase);
        foreach (var friend in list->CharDataSpan)
            AddName(found, friend.NameString);

        // Merged in case the list skips offline friends
        // TODO: check whether it does
        lock (_sync)
            _friends.UnionWith(found);
    }

    // TODO: FCs over 200 arrive a page at a time and we ask once, so big ones come back partial. very low priority.
    // True when it read a roster
    private static unsafe bool GatherFreeCompany()
    {
        // Only filled when the game asks the server, as opening the FC window does
        // After a character switch it can hold the previous company, hence the id check
        if (!TryRoster(out var list, out var companyId) || list->GetEntryCount() == 0 || (list->FreeCompanyId != 0 && list->FreeCompanyId != companyId))
            return false;

        HashSet<string> found = new(StringComparer.OrdinalIgnoreCase);
        foreach (var member in list->CharDataSpan)
            AddName(found, member.NameString);

        if (found.Count == 0)
            return false;

        lock (_sync)
        {
            _company.UnionWith(found);
            _companySeen = true;
        }

        return true;
    }

    private static bool _companySeen;

    internal static bool RequestCompanyRoster { get; set; }

    // A player opens that window a few times a session at most
    internal static readonly TimeSpan RosterCooldown = TimeSpan.FromMinutes(15);

    private static DateTime _nextRosterRequest = DateTime.MinValue;

    private static bool _rosterStale = true;

    // Same request as opening the FC window
    private static unsafe void MaybeRequestRoster()
    {
        if (!RequestCompanyRoster || !_rosterStale || DateTime.UtcNow < _nextRosterRequest || !Svc.InWorld || Svc.Condition[ConditionFlag.InCombat])
            return;

        // The window owns the buffer while it's up
        if (Svc.GameGui.GetAddonByName("FreeCompany") != nint.Zero || !TryRoster(out var list, out _))
            return;

        _nextRosterRequest = DateTime.UtcNow.Add(RosterCooldown);

        // One page, like opening the window
        // Paging through it all in a burst doesn't look like a player
        if (!list->RequestData())
            return;

        _rosterStale = false;
        Svc.Log.Debug("Asked the game for the free company roster.");

        // The answer comes over the network, so look again shortly
        // RosterCooldown stops that look asking again
        _wanted = true;
        _nextRefresh = DateTime.UtcNow.AddSeconds(5);
    }

    private static unsafe bool TryRoster(out InfoProxyFreeCompanyMember* list, out ulong companyId)
    {
        var company = InfoProxyFreeCompany.Instance();
        companyId = company == null ? 0 : company->Id;
        list = companyId == 0 ? null : InfoProxyFreeCompanyMember.Instance();
        return list != null;
    }

    // From Messenger's per-character logs: the file name is name@world, the timestamp the last tell
    private static void GatherTellPartners()
    {
        var config = ShippedSetup.PathOf("Messenger", "DefaultConfig.json");
        using var settings = File.Exists(config) ? JsonDocument.Parse(File.ReadAllText(config)) : null;

        // Messenger's LogStorageFolder when it has one
        var root = new DirectoryInfo(settings?.RootElement.TryGetProperty("LogStorageFolder", out var custom) == true && custom.GetString() is { Length: > 0 } folder
            ? folder
            : ShippedSetup.PathOf("Messenger"));

        if (!root.Exists)
            return;

        var cutoff = DateTime.UtcNow.AddDays(-TellPartnerDays);
        HashSet<string> found = new(StringComparer.OrdinalIgnoreCase);

        // One level down too, for Messenger's SplitLogging
        foreach (var file in root.EnumerateDirectories().Prepend(root).SelectMany(folder => folder.EnumerateFiles("*.txt")))
        {
            var stem = Path.GetFileNameWithoutExtension(file.Name);

            // Not its engagements (@Eng), superchannels (@Super) or channel logs (Party@)
            if (file.Length > 0 && file.LastWriteTimeUtc >= cutoff && stem.IndexOf('@') is > 0 and var at
                && stem[(at + 1)..] is not ("Eng" or "Super") && !Enum.IsDefined(typeof(XivChatType), stem[..at]))
                AddName(found, stem[..at]);
        }

        lock (_sync)
        {
            _tells.Clear();
            _tells.UnionWith(found);
        }
    }

    private static bool GatherParty()
    {
        HashSet<string> current = new(StringComparer.OrdinalIgnoreCase);

        foreach (var member in Svc.Party)
            AddName(current, member.Name.TextValue);

        lock (_sync)
        {
            // Against the party alone, so a name from chat doesn't read as a party change every tick
            if (_party.SetEquals(current))
                return false;

            _party.Clear();
            _party.UnionWith(current);
        }

        return true;
    }

    private static readonly HashSet<string> _party = new(StringComparer.OrdinalIgnoreCase);

    private static void OnChatMessage(IChatMessage message)
    {
        try
        {
            switch (message.LogKind)
            {
                // The held roster won't update itself, so this is the cue to look again
                case XivChatType.FreeCompanyLoginLogout:
                    _rosterStale = true;
                    Invalidate();
                    return;

                case XivChatType.TellIncoming:
                case XivChatType.TellOutgoing:
                    Remember(_tells, SpeakerName(message.Sender));
                    return;
            }

            if (SpeakerName(message.Sender) is { } speaker && MentionsMe(message.Message.TextValue))
                Remember(_present, speaker);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "Could not read a chat message for player names.");
        }
    }

    private static void Remember(HashSet<string> tier, string? name)
    {
        // The sets hold first and last names apart, so this is the only reliable newness test
        lock (_sync)
            if (!AddName(tier, name))
                return;

        Publish();
    }

    private static string? SpeakerName(SeString sender) => sender.Payloads.OfType<PlayerPayload>().FirstOrDefault()?.PlayerName;

    private static bool MentionsMe(string text) =>
        text.Length > 0 && Svc.Objects.LocalPlayer?.Name.TextValue is { } me
        && me.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(part => part.Length >= ShortestName && HasWord(text, part));

    // "Rin" in "Rin's", not in "during"
    private static bool HasWord(string text, string word)
    {
        for (var at = text.IndexOf(word, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.OrdinalIgnoreCase))
            if ((at == 0 || !char.IsLetter(text[at - 1])) && (at + word.Length == text.Length || !char.IsLetter(text[at + word.Length])))
                return true;

        return false;
    }

    private static bool AddName(HashSet<string> into, string? full)
    {
        var added = false;

        foreach (var part in full?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [])
        {
            var trimmed = part.Trim('.', ',', '!', '?');

            foreach (var word in GameText.WithHalves(trimmed))
                if (word.Length >= ShortestName && word.All(GameText.IsWordChar))
                    added |= into.Add(word);
        }

        return added;
    }
}
