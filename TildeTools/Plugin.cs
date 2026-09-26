using System;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Lumina.Excel.Sheets;
using TildeTools.Modules;
using TildeTools.Modules.ChatTwo;
using TildeTools.Modules.EmoteSplitter;
using TildeTools.Modules.EmoteSplitter.Chat;
using TildeTools.Modules.Messenger;
using TildeTools.Modules.Spelling;
using TildeTools.Modules.Wordsmith;
using TildeTools.Ipc;
using TildeTools.Ui;

namespace TildeTools;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/tt";

    private readonly Configuration _config;
    private readonly ModuleManager _modules;
    private readonly MainWindow _window;
    private readonly SetupWindow _setup;
    private readonly EmoteSplitterModule _emoteSplitter;
    // Null until the constructor's end: the splitter's first tick and a save can come before it
    private readonly SplitterIpc? _ipc;
    private readonly NativeChatSpelling _nativeSpelling;

    private readonly WindowSystem _windows = new("TildeTools");

    // Spelling's broadcast that marks are stale, the one Chat 2 and Messenger take too
    private static readonly Lazy<ICallGateSubscriber<object?>> AvailableGate =
        new(() => Svc.Pi.GetIpcSubscriber<object?>("TildeTools.Spell.Available"));

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Svc>();

        ChannelCommands.AddClientNames(Svc.Data.GetExcelSheet<TextCommand>()
            .Select(c => new[] { c.Command, c.ShortCommand, c.Alias, c.ShortAlias }.Select(n => n.ExtractText())));

        _config = Configuration.Load();

        ShippedSetup.Declined = _config.DeclinedSetups;

        _modules = new ModuleManager(_config, Save);
        _setup = new SetupWindow(_modules, _config, Save);
        _window = new MainWindow(_modules, _setup);
        _windows.AddWindow(_setup);
        _windows.AddWindow(_window);

        _emoteSplitter = new EmoteSplitterModule(_config.EmoteSplitter, Save, () => _ipc?.Announce(), _windows);
        _modules.Register(_emoteSplitter, onGameThread: true);

        // Not Save: nothing in Spelling changes what SplitterIpc answers
        var spelling = new SpellingModule(_config.Spelling, () => Svc.Pi.SavePluginConfig(_config), _windows);
        _modules.Register(spelling);

        var chatTwo = new ChatTwoModule(_config.ChatTwo, Save, () => _emoteSplitter.IsEnabled);
        _modules.Register(chatTwo);
        ChatSender.EncodeTags = chatTwo.EncodeTags;
        _modules.Register(new MessengerModule());
        _modules.Register(new WordsmithModule());

        _nativeSpelling = new NativeChatSpelling(() => spelling.Ipc);

        AvailableGate.Value.Subscribe(_nativeSpelling.Recheck);
        _windows.AddWindow(_nativeSpelling.Menu);

        _ipc = new SplitterIpc(_emoteSplitter, () => chatTwo.InputByteCap);

        // Before _windows.Draw, which reads the _menuWanted and tab these set
        Svc.Pi.UiBuilder.Draw += _nativeSpelling.Draw;
        Svc.Pi.UiBuilder.Draw += _window.Redirect;
        Svc.Pi.UiBuilder.Draw += _windows.Draw;
        Svc.Pi.UiBuilder.OpenConfigUi += _window.Open;
        Svc.Pi.UiBuilder.OpenMainUi += _window.Open;

        Svc.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open TildeTools\n" +
                "/tt setup - Run the first-time setup again\n" +
                "/tt cancel - Stop a message that's mid-send",
        });

        Save();

        if (!_config.SetupSeen)
            _setup.Open();
    }

    private void Save()
    {
        Svc.Pi.SavePluginConfig(_config);

        _ipc?.Announce();
    }

    private void OnCommand(string command, string args)
    {
        var sub = args.Trim();

        if (sub.Equals("cancel", StringComparison.OrdinalIgnoreCase))
            _emoteSplitter.Stop();
        else if (sub.Equals("setup", StringComparison.OrdinalIgnoreCase))
            _setup.Open();
        else
            _window.Toggle();
    }

    public void Dispose()
    {
        Svc.Pi.UiBuilder.Draw -= _nativeSpelling.Draw;
        Svc.Pi.UiBuilder.Draw -= _window.Redirect;
        Svc.Pi.UiBuilder.Draw -= _windows.Draw;
        Svc.Pi.UiBuilder.OpenConfigUi -= _window.Open;
        Svc.Pi.UiBuilder.OpenMainUi -= _window.Open;

        Svc.Commands.RemoveHandler(CommandName);

        AvailableGate.Value.Unsubscribe(_nativeSpelling.Recheck);
        _windows.RemoveAllWindows();
        _ipc?.Dispose();
        _modules.Dispose();

        // Not Save(), which announces on the gates just disposed
        Svc.Pi.SavePluginConfig(_config);
    }
}
