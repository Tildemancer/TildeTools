using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using TildeTools.Ui;

namespace TildeTools.Modules;

// setup: the settings I ship for it, and where it keeps its own
internal abstract class HostedModule<T>(string plugin, string internalName, Action prepare, SetupFile? setup = null) : IModule
    where T : class, IDisposable
{
    protected T? Instance { get; private set; }

    protected bool IsRunning => Instance != null;

    private string? FailureReason { get; set; }

    public abstract string Id { get; }

    public virtual string Name => plugin;

    public abstract string Description { get; }

    public bool IsEnabled { get; private set; }

    // Cached: asked every frame, and InstalledPlugins locks and copies Dalamud's list
    private string? _conflict = Conflict(plugin, internalName);

    public virtual string? UnavailableReason => _conflict;

    public void PluginsChanged() => _conflict = Conflict(plugin, internalName);

    private static string? Conflict(string plugin, string internalName) => IsInstalled(internalName, loaded: true)
        ? $"{plugin} is installed as its own plugin. Disable it there to run this copy."
        : null;

    // Asks Dalamud, not the loaded assemblies: an unloaded plugin's assembly lingers until collected
    private static bool IsInstalled(string internalName, bool loaded = false)
    {
        try
        {
            return Svc.Pi.InstalledPlugins.Any(plugin => (!loaded || plugin.IsLoaded) && plugin.InternalName == internalName);
        }
        catch (Exception ex)
        {
            // Allow rather than block on a failed check
            Svc.Log.Warning(ex, $"Could not tell whether {internalName} is installed separately.");
            return false;
        }
    }

    public bool StartsOnFirstRun => setup is not { } file || IsInstalled(internalName) || File.Exists(file.Path);

    public SetupFile? Setup => setup;

    public void Enable()
    {
        // First: Chat 2 reads its cap while it constructs
        IsEnabled = true;
        (FailureReason, _drawFailed) = (null, false);

        try
        {
            if (setup is { } file)
            {
                ShippedSetup.Prepare(file);
                _queued = false;
            }

            prepare();

            Instance = Svc.Pi.Create<T>() ?? throw new InvalidOperationException($"Dalamud could not construct {plugin}.");

            AfterCreate();
            Svc.Log.Info($"{plugin} started.");

            // Messenger registers its commands from a TickScheduler
            Svc.Framework.RunOnTick(HideCommands, delayTicks: 2);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"{plugin} failed to start.");

            // Dalamud's Create wraps a constructor's throw a few layers deep, the innermost says why
            var cause = ex;
            while (cause.InnerException is { } inner)
                cause = inner;

            FailureReason = cause.Message;

            // Left enabled, so the tab shows why and the preference survives
            Stop();
        }
    }

    // Its commands go through our command manager, so the installer and /xlhelp would list them as TildeTools'
    private void HideCommands()
    {
        if (!IsRunning)
            return;

        foreach (var info in Svc.Commands.Commands.Values)
            if (info is CommandInfo command && command.Handler.Method.Module.Assembly == typeof(T).Assembly)
                command.ShowInHelp = false;
    }

    // Inside Enable's try, so a throw here is a failed start
    protected virtual void AfterCreate() { }

    // Instance still set: what its own Dispose leaves on our interface
    protected virtual void AfterDispose() { }

    // Out of its plugin's window system, so opening it only asks for our tab
    // See MainWindow.Redirect
    protected virtual Window? Settings => null;

    public bool TakeSettingsRequest()
    {
        if (Settings is not { IsOpen: true } window)
            return false;

        window.IsOpen = false;
        return true;
    }

    private bool _closing;

    // Logged once a start, not every frame the tab shows
    private bool _drawFailed;

    public bool TakeCloseRequest()
    {
        var closing = _closing;
        _closing = false;
        return closing;
    }

    // Wordsmith reloads its values on open
    public void SettingsOpened() => Settings?.OnOpen();

    // Messenger saves its settings when their window closes
    public void SettingsClosed() => Settings?.OnClose();

    private void DrawSettings()
    {
        // Their Draw only emits content, so it renders fine in our tab
        if (Settings is not { } window)
            return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted($"{plugin} settings");
        ImGui.Spacing();

        using var id = ImRaii.PushId("hosted-settings");

        // Its own child, so ImGui.IsWindowAppearing is our tab showing: Chat 2 reloads its copy of the config on that
        using var child = ImRaii.Child("##hosted-settings", Vector2.Zero);
        if (!child.Success)
            return;

        // Open while drawn, so their own Close shows as it dropping
        window.IsOpen = true;
        try
        {
            window.Draw();
            _closing = !window.IsOpen;
        }
        catch (Exception ex)
        {
            if (!_drawFailed)
                Svc.Log.Error(ex, $"{plugin}'s settings failed to draw.");

            _drawFailed = true;
            ImGui.TextColored(Widgets.ErrorColour, $"{plugin}'s settings could not be drawn.");
        }
        finally
        {
            window.IsOpen = false;
        }
    }

    public void Disable()
    {
        Stop();
        IsEnabled = false;
    }

    private void Stop()
    {
        if (Instance == null)
            return;

        try
        {
            // A dirty stop is when the give-back matters most
            try
            {
                Instance.Dispose();
            }
            finally
            {
                AfterDispose();
            }

            Svc.Log.Info($"{plugin} stopped.");
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"{plugin} did not shut down cleanly.");
        }
        finally
        {
            Instance = null;
        }
    }

    public void DrawTab()
    {
        using (ImRaii.TextWrapPos(0f))
        {
            if (FailureReason != null)
                ImGui.TextColored(Widgets.ErrorColour, $"{plugin} failed to start: {FailureReason}");

            DrawBody();
        }

        DrawShippedSetup();
        DrawSettings();
    }

    protected abstract void DrawBody();

    private bool _replacing;
    private bool _queued;

    public void UseShippedSetup()
    {
        if (setup is not { } file)
            return;

        ShippedSetup.Queue(file);

        // Not inside a window's Draw, see MainWindow.DrawModuleList
        // Checks IsEnabled: RunOnTick isn't cancelled by Disable or unload
        if (file.Restarts && IsRunning)
            Svc.Framework.RunOnTick(() =>
            {
                if (!IsEnabled)
                    return;

                Disable();
                Enable();
            });
        else
            _queued = true;
    }

    private void DrawShippedSetup()
    {
        if (setup is not { } file)
            return;

        using var wrap = ImRaii.TextWrapPos(0f);
        ImGui.Separator();

        if (_queued)
        {
            ImGui.TextDisabled(file.Restarts
                ? $"Tildemancer's defaults are queued, and take over when {plugin} next starts."
                : "Tildemancer's defaults take over the next time TildeTools loads: reload it in /xlplugins or restart the game.");
            return;
        }

        if (!_replacing)
        {
            if (ImGui.Button("Use Tildemancer's defaults"))
                _replacing = true;

            return;
        }

        ImGui.TextUnformatted($"Replace your {plugin} settings with Tildemancer's defaults? Yours are kept as a .bak file.");

        if (ImGui.Button("Replace them"))
        {
            _replacing = false;
            UseShippedSetup();
        }

        ImGui.SameLine();

        if (ImGui.Button("Cancel"))
            _replacing = false;
    }

    public void Dispose() => Disable();
}
