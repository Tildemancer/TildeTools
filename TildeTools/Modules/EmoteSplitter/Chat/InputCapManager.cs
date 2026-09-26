using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace TildeTools.Modules.EmoteSplitter.Chat;

// The game rebuilds the component and resets the cap, so this re-applies on addon events
internal sealed unsafe class InputCapManager : IDisposable
{
    private readonly EmoteSplitterSettings _settings;

    private uint _originalMaxByte;
    private uint _originalMaxChar;
    private bool _captured;

    internal static readonly AddonEvent[] ChatLogEvents = [AddonEvent.PostSetup, AddonEvent.PostRefresh, AddonEvent.PostRequestedUpdate];

    internal static bool Available =>
        AtkComponentTextInput.MemberFunctionPointers.SetMaxByte != null && AtkComponentTextInput.MemberFunctionPointers.SetMaxChar != null;

    internal InputCapManager(EmoteSplitterSettings settings)
    {
        _settings = settings;

        // Once, not on every settings change
        if (!Available)
            Svc.Log.Warning("SetMaxByte/SetMaxChar could not be located for this game version; leaving the chat input limit alone.");

        foreach (var ev in ChatLogEvents)
            Svc.AddonLife.RegisterListener(ev, "ChatLog", OnChatLogChanged);

        Apply();
    }

    private void OnChatLogChanged(AddonEvent type, AddonArgs args) => Apply();

    internal void Apply()
    {
        if (!_settings.UnlockChatInput || !Available)
        {
            Restore();
            return;
        }

        var input = ChatSender.ChatLogInput();
        if (input == null)
            return;

        if (!_captured)
        {
            (_originalMaxByte, _originalMaxChar, _captured) = (input->ComponentTextData.MaxByte, input->ComponentTextData.MaxChar, true);
            Svc.Log.Info($"Chat input native caps: MaxByte={_originalMaxByte}, MaxChar={_originalMaxChar}");
        }

        // Clamped by UnlockedMaxBytes' setter
        var targetBytes = _settings.UnlockedMaxBytes;
        if (input->ComponentTextData.MaxByte == targetBytes)
            return;

        input->SetMaxByte(targetBytes);

        // MaxChar=0 means no limit, so setting one would add a limit
        if (_originalMaxChar != 0)
            input->SetMaxChar(targetBytes);

        var applied = input->ComponentTextData.MaxByte;
        if (applied == targetBytes)
            Svc.Log.Info($"Chat input limit set to {applied} bytes.");
        else
            Svc.Log.Warning($"Set the chat input limit to {targetBytes} bytes but it still reads {applied}.");
    }

    private void Restore()
    {
        if (!_captured || !Available)
            return;

        var input = ChatSender.ChatLogInput();
        if (input == null)
            return;

        input->SetMaxByte((int)_originalMaxByte);
        input->SetMaxChar((int)_originalMaxChar);

        // Or every ChatLog event while unlocking is off would set them again
        _captured = false;
    }

    public void Dispose()
    {
        Svc.AddonLife.UnregisterListener(OnChatLogChanged);
        Restore();
    }
}
