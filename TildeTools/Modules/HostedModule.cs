using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using TildeTools.Ui;

namespace TildeTools.Modules;

// setup: my default settings for it, and where it keeps its own.
internal abstract class HostedModule<T>(string plugin, string internalName, Action prepare, SetupFile? setup = null) : IModule
    where T : class, IDisposable
{
    protected T? Instance { get; private set; }

    protected bool IsRunning => Instance != null;

    private string? FailureReason { get; set; }

    public abstract string Id { get; }

    public string Name => plugin;

    public abstract string Description { get; }

    public bool IsEnabled { get; private set; }

    // Cached because it's asked every frame, and InstalledPlugins locks and copies Dalamud's list.
    private string? _conflict = Conflict(plugin, internalName);

    public virtual string? UnavailableReason => _conflict;

    public void PluginsChanged() => _conflict = Conflict(plugin, internalName);

    // Asks Dalamud instead of checking loaded assemblies, since an unloaded plugin's assembly hangs around until it's collected.
    private static string? Conflict(string plugin, string internalName) =>
        Svc.Pi.InstalledPlugins.Any(installed => installed.IsLoaded && installed.InternalName == internalName)
            ? $"{plugin} is installed as its own plugin. Disable it there to run this version!"
            : null;

    public bool StartsOnFirstRun => setup is not { } file || file.HasSettings;

    public SetupFile? Setup => setup;

    public void Enable()
    {
        // Has to go first because C2 reads its input cap while it's constructing.
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

            // Two ticks late on purpose, XIM registers its commands from a TickScheduler.
            Svc.Framework.RunOnTick(HideCommands, delayTicks: 2);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"{plugin} failed to start.");

            // Dalamud's Create wraps a constructor's throw a few layers deep, the innermost reports why it threw.
            var cause = ex;
            while (cause.InnerException is { } inner)
                cause = inner;

            FailureReason = cause.Message;

            // Stays enabled so the tab can show why, and the setting doesn't get lost.
            Stop();
        }
    }

    // Its commands register through our command manager, so without this the installer and /xlhelp list them as TT's commands.
    private void HideCommands()
    {
        if (!IsRunning)
            return;

        foreach (var info in Svc.Commands.Commands.Values)
            if (info is CommandInfo command && command.Handler.Method.Module.Assembly == typeof(T).Assembly)
                command.ShowInHelp = false;
    }

    // Runs inside Enable's try, so throwing here counts as a failed start for our purposes.
    protected virtual void AfterCreate() { }

    // Instance is still set, for anything its Dispose leaves behind on our plugin interface.
    // Null if it wasn't running or its construction threw, see Stop
    protected virtual void AfterDispose() { }

    // Taken out of its own window system, so opening it just asks for our tab.
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

    // Broken Draw logs once per start, otherwise it would spam itself every frame.
    private bool _drawFailed;

    public bool TakeCloseRequest()
    {
        var closing = _closing;
        _closing = false;
        return closing;
    }

    // WS reloads its values when this opens.
    public void SettingsOpened() => Settings?.OnOpen();

    // XIM only saves its settings when their window closes.
    public void SettingsClosed() => Settings?.OnClose();

    private void DrawSettings()
    {
        // Their Draw only draws content (no Begin/End), so it works fine inside our tab.
        if (Settings is not { } window)
            return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted($"{plugin} settings");
        ImGui.Spacing();

        using var id = ImRaii.PushId("hosted-settings");

        // Its own child, so IsWindowAppearing fires when our tab shows, and C2 reloads its config copy on that.
        using var child = ImRaii.Child("##hosted-settings", Vector2.Zero);
        if (!child.Success)
            return;

        // Marked open while drawing, so if their own Close button fires, we see IsOpen drop.
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
            ImGui.TextColored(Widgets.ErrorColour, $"{plugin}'s settings could not be drawn. Try reloading!");
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
        // A construction that threw can still have left its flags on our UiBuilder.
        if (Instance == null)
        {
            AfterDispose();
            return;
        }

        try
        {
            // In a finally, a Dispose that throws still needs to be cleaned up.
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
                ImGui.TextColored(Widgets.ErrorColour, $"{plugin} failed to start, send me this: {FailureReason}");

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

        try
        {
            ShippedSetup.Queue(file);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"Could not write my setup for {plugin}.");
            return;
        }

        // Not mid-Draw, see MainWindow.DrawModuleList
        // Checks IsEnabled first, RunOnTick doesn't get cancelled by Disable or unload.
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
                ? $"Defaults are queued, and take over when {plugin} next starts. Try reloading!"
                : "Tildemancer's defaults take over the next time TildeTools loads: reload it in /xlplugins or restart the game.");
            return;
        }

        if (!_replacing)
        {
            if (ImGui.Button("Use Tildemancer's defaults"))
                _replacing = true;

            return;
        }

        ImGui.TextUnformatted($"Replace your {plugin} settings with my defaults? Yours are kept as a .bak file.");

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
