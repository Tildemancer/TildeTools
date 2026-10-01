using System;
using System.Collections.Generic;
using Dalamud.Plugin;

namespace TildeTools.Modules;

// Fails safe both ways:
// If it throws going up, it's left off
// If it throws going down, it stays marked on so Dispose tries it cleaning up again
internal sealed class ModuleManager(Configuration config, Action save) : IDisposable
{
    private readonly List<IModule> _modules = [];

    private bool _disposed;

    // Done so a module starts itself once a conflicting install goes, no restart needed!
    // Fires after every Register from Dalamud's load thread while Reconcile walks the modules on the framework thread.
    // One Reconcile for a plugin that came or went in the meantime.
    // That first Reconcile ALSO starts the game-thread modules, then saves, with the save announcing so that C2 rereads its cap properly.
    internal void Watch()
    {
        Svc.Pi.ActivePluginsChanged += OnActivePluginsChanged;
        Svc.Framework.RunOnTick(() =>
        {
            if (_disposed)
                return;

            Reconcile();
            save();
        });
    }

    // Deferred to the next tick.
    // Dalamud raises this from its load and unload tasks on any thread inside its plugin list lock.
    private void OnActivePluginsChanged(IActivePluginsChangedEventArgs args) => Svc.Framework.RunOnTick(Reconcile);

    private void Reconcile()
    {
        // On exit, each plugin that unloads before us clears its conflict, which would start the TT version.
        if (Svc.Framework.IsFrameworkUnloading)
            return;

        foreach (var module in _modules)
        {
            module.PluginsChanged();

            // See TryEnable
            if (module.IsEnabled && module.UnavailableReason is { } reason)
            {
                Svc.Log.Info($"Stopping {module.Name}: {reason}");
                TryDisable(module);
            }
            else if (module.UnavailableReason == null && config.IsModuleEnabled(module.Id))
            {
                TryEnable(module);
            }
        }
    }

    // A List so the windows' per-frame loops get its struct enumerator without boxing.
    internal List<IModule> Modules => _modules;

    // onGameThread's Enable touches the game's UI, and Dalamud runs the plugin's constructor off that thread.
    // Left to Watch's first Reconcile, unless it's unavailable.
    // TryEnable will log why.
    internal void Register(IModule module, bool onGameThread = false)
    {
        _modules.Add(module);

        // C2 and XIM only start for people who had them before, so nobody gets a new unsolicited chatbox.
        if (config.FirstRun)
            config.SetModuleEnabled(module.Id, module.StartsOnFirstRun);

        if (config.IsModuleEnabled(module.Id) && (!onGameThread || module.UnavailableReason != null))
            TryEnable(module);
    }

    private void TryEnable(IModule module)
    {
        if (module.IsEnabled)
            return;

        if (module.UnavailableReason is { } reason)
        {
            Svc.Log.Info($"Not enabling {module.Name}: {reason}");
            return;
        }

        try
        {
            module.Enable();
            Svc.Log.Info($"Module enabled: {module.Name}");
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"{module.Name} failed to start; leaving it off.");

            // Off in the config too, or Reconcile would retry it on every plugin change and every session.
            // Every caller but a plugin change saves after, and Plugin.Dispose saves on the way out.
            config.SetModuleEnabled(module.Id, false);

            try
            {
                module.Disable();
            }
            catch (Exception inner)
            {
                Svc.Log.Error(inner, $"{module.Name} also failed to clean up.");
            }
        }
    }

    private void TryDisable(IModule module)
    {
        if (!module.IsEnabled)
            return;

        try
        {
            module.Disable();
            Svc.Log.Info($"Module disabled: {module.Name}");
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"{module.Name} failed to shut down cleanly.");
        }
    }

    internal void SetEnabled(IModule module, bool enabled)
    {
        // MainWindow queues this with RunOnTick, which unload doesn't cancel.
        if (_disposed)
            return;

        if (enabled)
            TryEnable(module);
        else
            TryDisable(module);

        // A blocked module keeps the user's choice, so it starts once it's unblocked. Any enable that throws defaults to off so it doesn't constantly retry every session.
        // Hosted ones stay on, see HostedModule.Enable
        var blocked = module.UnavailableReason != null;
        config.SetModuleEnabled(module.Id, blocked ? enabled : module.IsEnabled);

        // The plugin's save announces too so C2 knows to reread its cap when the splitter goes on or off.
        save();
    }

    public void Dispose()
    {
        _disposed = true;
        Svc.Pi.ActivePluginsChanged -= OnActivePluginsChanged;

        // Reverse order, so the layered modules come off first.
        for (var i = _modules.Count - 1; i >= 0; i--)
        {
            try
            {
                _modules[i].Dispose();
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, $"{_modules[i].Name} threw while being disposed.");
            }
        }

        _modules.Clear();
    }
}
