using System;
using System.Collections.Generic;
using Dalamud.Plugin;

namespace TildeTools.Modules;

// Fails safe both ways
// Throws going up: left off
// Throws going down: still marked on, so Dispose tries to clean again
internal sealed class ModuleManager : IDisposable
{
    private readonly List<IModule> _modules = [];
    private readonly Configuration _config;
    private readonly Action _save;

    private bool _disposed;

    internal ModuleManager(Configuration config, Action save)
    {
        _config = config;
        _save = save;

        // So a module starts itself once a conflicting install goes, no restart
        Svc.Pi.ActivePluginsChanged += OnActivePluginsChanged;
    }

    // Next tick: Dalamud raises it from its load and unload tasks, on whichever thread, inside its plugin list lock
    private void OnActivePluginsChanged(IActivePluginsChangedEventArgs args) => Svc.Framework.RunOnTick(Reconcile);

    private void Reconcile()
    {
        // At exit each plugin unloaded ahead of us clears its conflict, which would start our copy
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
            else if (module.UnavailableReason == null && _config.IsModuleEnabled(module.Id))
            {
                TryEnable(module);
            }
        }
    }

    // A List, so the windows' per-frame loops take its struct enumerator unboxed
    internal List<IModule> Modules => _modules;

    // onGameThread: its Enable touches the game's UI, and Dalamud runs the plugin's constructor off that thread
    // Put off to the next tick, then saved, which announces, so Chat 2 rereads its cap
    internal void Register(IModule module, bool onGameThread = false)
    {
        _modules.Add(module);

        // No new chat box unasked: Chat 2 and Messenger start only for someone who had them
        if (_config.FirstRun)
            _config.SetModuleEnabled(module.Id, module.StartsOnFirstRun);

        if (!_config.IsModuleEnabled(module.Id))
            return;

        if (!onGameThread)
            TryEnable(module);
        else
            Svc.Framework.RunOnTick(() =>
            {
                if (_disposed)
                    return;

                TryEnable(module);
                _save();
            });
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
        // MainWindow queues this with RunOnTick, which unload doesn't cancel
        if (_disposed)
            return;

        if (enabled)
            TryEnable(module);
        else
            TryDisable(module);

        // Blocked keeps the user's choice, so it starts once unblocked
        // A failed Enable that throws is recorded off, so it doesn't retry every session
        // A hosted one stays on, see HostedModule.Enable
        var blocked = module.UnavailableReason != null;
        _config.SetModuleEnabled(module.Id, blocked ? enabled : module.IsEnabled);

        // The plugin's save, which also announces, so Chat 2 rereads its cap when the splitter goes on or off
        _save();
    }

    public void Dispose()
    {
        _disposed = true;
        Svc.Pi.ActivePluginsChanged -= OnActivePluginsChanged;

        // Reverse order, so layered modules come off first
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
