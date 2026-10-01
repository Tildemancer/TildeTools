using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace TildeTools.Modules.Spelling;

// _present is only accepted and never suggested.
// Most of the people it catches are strangers, and rare surnames hijack ordinary typos.
internal static class PlayerVocabulary
{
    // FFXIV allows 2 letters, but a name that short can't be told from a real word, so...
    private const int ShortestName = 3;

    // Re-reads are event-driven, this just stops a burst of FC logins costing one each.
    private const int QuietSeconds = 20;

    private static readonly HashSet<string> _friends = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _company = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _tells = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _present = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Lock _sync = new();

    private static DateTime _nextRefresh = DateTime.MinValue;
    private static bool _listening;
    private static bool _tellsStale;

    // A new window has to reread the logs, or the change waits for a login.
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

    // now skips QuietSeconds for a settings change the player is looking at.
    internal static void Invalidate(bool now = false)
    {
        _wanted = true;

        if (now)
            _nextRefresh = DateTime.MinValue;
    }

    private static bool _wanted;

    // Rebuilt only when a count or the roster hint changes, since the tab draws it every frame.
    private static (int Friends, int Company, int Tells, int Session, int Hint) _summaryKey = (-1, 0, 0, 0, 0);
    private static string _summary = string.Empty;

    internal static unsafe string Summary()
    {
        lock (_sync)
        {
            // An empty roster looks broken, and when we're not asking, only the player can fix it.
            var hint = _companySeen || !TryRoster(out _, out _) ? 0 : RequestCompanyRoster ? 1 : 2;
            var key = (_friends.Count, _company.Count, _tells.Count, _present.Count + _party.Count, hint);
            if (key == _summaryKey)
                return _summary;

            _summaryKey = key;
            var summary = key is (0, 0, 0, 0, _)
                ? "No names learned yet."
                : $"Names learned: {key.Item1} friends, {key.Item2} FC mates, {key.Item3} from tells, and " +
                  $"{key.Item4} people mentioned you by name or shared a party with you this " +
                  "session.";

            return _summary = hint switch
            {
                1 => summary + " Waiting on the free company roster.",
                2 => summary + " Open your Free Company window once to load its roster.",
                _ => summary,
            };
        }
    }

    internal static void Start()
    {
        Svc.Chat.ChatMessageHandled += OnChatMessage;
        Svc.Chat.ChatMessageUnhandled += OnChatMessage;
        Svc.ClientState.Login += OnLogin;
        Svc.Framework.Update += OnUpdate;
        _listening = true;

        // The one full read, after this the changes come from the chat log.
        // Queued for the next Tick on the game's thread, because Start can run in the plugin's constructor, off that thread.
        (_wanted, _tellsStale, _nextRefresh) = (true, true, DateTime.MinValue);
    }

    private static void OnLogin()
    {
        Forget();

        // Only a new login skips RosterCooldown, a stop and start of name learning waits it out.
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

        lock (_sync)
            Speller.SetPeople([], []);
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

        (_friendsRead, _membersRead, _rosterStale) = (0, 0, true);
    }

    private static double _sinceLook;

    // Every couple of seconds, not every frame.
    // Party changes are rare, and the costly sources have their own timer, QuietSeconds.
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
        // Every tick, so one skipped for combat or loading goes out as soon as that ends.
        MaybeRequestRoster();

        // Both lists fill some time after login, the roster when the FC window opens, and (I think?) one page at a time.
        var changed = GatherParty() | GatherFriends(again: false) | GatherFreeCompany(again: false);

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
        GatherFriends(again: true);
        GatherFreeCompany(again: true);

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

    // Under _sync with Stop's clear, so a tell rescan finishing after it can't put the names back.
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

    // Each list's size as of the last read, a tick only walks a list again when its size changed. 'True' walks it anyway, because size can't tell between two different friends since it just counts.
    private static uint _friendsRead, _membersRead;

    // It's true when it read the list. Caller calls it 'changed' but since it technically runs Publish() even when it reads the same names...
    private static unsafe bool GatherFriends(bool again)
    {
        var list = InfoProxyFriendList.Instance();
        if (list == null || !again && list->EntryCount == _friendsRead)
            return false;

        _friendsRead = list->EntryCount;
        HashSet<string> found = new(StringComparer.OrdinalIgnoreCase);
        foreach (var friend in list->CharDataSpan)
            AddName(found, friend.NameString);

        // Merged, as a read mid-load comes up short.
        // DOES list offline friends too.
        lock (_sync)
            _friends.UnionWith(found);

        return true;
    }

    // TODO: FCs over 200 arrive a page at a time and we ask once, so big ones can come back partial. very low priority.
    // True when it read the roster.
    private static unsafe bool GatherFreeCompany(bool again)
    {
        // Only filled when the game asks the server, as opening the FC window does.
        // After a character switch it can be stale via holding the previous company, hence the id check.
        if (!TryRoster(out var list, out var companyId) || (list->FreeCompanyId != 0 && list->FreeCompanyId != companyId))
            return false;

        var count = list->GetEntryCount();
        if (count == 0 || !again && count == _membersRead)
            return false;

        _membersRead = count;
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

    // A player opens the window a few times a session at most
    internal static readonly TimeSpan RosterCooldown = TimeSpan.FromMinutes(15);

    private static DateTime _nextRosterRequest = DateTime.MinValue;

    private static bool _rosterStale = true;

    // Same request as opening the FC window.
    private static unsafe void MaybeRequestRoster()
    {
        if (!RequestCompanyRoster || !_rosterStale || DateTime.UtcNow < _nextRosterRequest || !Svc.InWorld || Svc.Condition[ConditionFlag.InCombat])
            return;

        // The window owns the buffer while it's up.
        if (Svc.GameGui.GetAddonByName("FreeCompany") != nint.Zero || !TryRoster(out var list, out _))
            return;

        _nextRosterRequest = DateTime.UtcNow.Add(RosterCooldown);

        // One page, like opening the window, paging through it all in a burst won't look like a player.
        if (!list->RequestData())
            return;

        _rosterStale = false;
        Svc.Log.Debug("Asked the game for the free company roster.");

        // The answer comes over the network, so we recheck shortly.
        // RosterCooldown stops that look asking again.
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

    // From XIM's per-character logs, where the file name is name@world and its timestamp is the last tell.
    private static void GatherTellPartners()
    {
        var config = ShippedSetup.PathOf("Messenger", "DefaultConfig.json");
        using var settings = File.Exists(config) ? JsonDocument.Parse(File.ReadAllText(config)) : null;

        // XIM's LogStorageFolder when it has one.
        var root = new DirectoryInfo(settings?.RootElement.TryGetProperty("LogStorageFolder", out var custom) == true && custom.GetString() is { Length: > 0 } folder
            ? folder
            : ShippedSetup.PathOf("Messenger"));

        if (!root.Exists)
            return;

        var cutoff = DateTime.UtcNow.AddDays(-TellPartnerDays);
        HashSet<string> found = new(StringComparer.OrdinalIgnoreCase);

        // One level down too, for XIM's SplitLogging.
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
            // Compared against the party alone, so a name from chat doesn't read as a party change every tick.
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
                // This is the cue to look again since the held roster won't update itself.
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
        // This is the only reliable newness test since sets hold first and last names apart.
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
