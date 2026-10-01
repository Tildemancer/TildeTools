using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TildeTools.Modules.EmoteSplitter.Chat;

namespace TildeTools.Modules.Spelling;

// Spelling's module, null while off.
internal sealed unsafe class NativeChatSpelling(Func<SpellIpc?> speller)
{
    // ABGR: red
    private const uint Color = 0xFF4040FFu;

    private const float Drop = 1f;
    private const float Thickness = 1.5f;

    private string _lastText = string.Empty;
    private List<(int Start, int Length)> _lastMarks = [];

    private readonly List<(float Left, float Right, float Top, float Bottom, int Start, int Length)> _drawn = [];

    private readonly Dictionary<int, float> _widths = [];
    private (string Text, byte Size, byte Font, float W, float H) _widthsFor;

    private byte[] _rawSeen = [];
    private string _textSeen = string.Empty;

    private string _pendingWord = string.Empty;

    // A right-click has moved the caret by the time the menu opens, so we get the last frame's position to compensate.
    private int _caretLastFrame = -1;
    private (int Start, int End) _selectionLastFrame = (-1, -1);
    private int _caretAtOpen = -1;
    private bool _pendingMisspelled;
    // Set as the menu opens, so replace isn't made every frame.
    // SpellMenu keeps each id's synonyms and corrections.
    private string _menuId = string.Empty;
    private int _menusOpened;
    private Action<string> _replace = _ => { };

    private const int FailuresAllowed = 10;
    private int _failures;
    private bool _off;

    private const int LeftButton = 0x01, RightButton = 0x02, Escape = 0x1B, Insert = 0x2D;
    private bool _triggerHeld;
    private bool _rightHeld;

    // Asks Windows, because neither ImGui nor the game's key array apparently sees input while the chat box has focus.
    // There has got to be a better way to do this...
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point point);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(nint window, ref System.Drawing.Point point);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    // From Windows again, since ImGui's position goes stale over the game's UI. Curse of imgui -- yeah okay
    private static Vector2 PointerPosition() =>
        GetCursorPos(out var point) && GetForegroundWindow() is var window && window != nint.Zero && ScreenToClient(window, ref point)
            ? new Vector2(point.X, point.Y)
            : ImGui.GetIO().MousePos;

    private static bool Inside(Vector2 p, float left, float top, float right, float bottom) =>
        p.X >= left && p.X <= right && p.Y >= top && p.Y <= bottom;

    // Button and pointer both come from Windows.
    // The event system crashes here. How fun.
    private void WatchForRightClick(string text)
    {
        if (!PressedNow(Down(RightButton), ref _rightHeld))
            return;

        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow))
            return;

        var mouse = PointerPosition();
        var index = Inside(mouse, _rect.X, _rect.Y, _rect.X + _rect.W, _rect.Y + _rect.H) ? IndexAt(text, mouse.X - _rect.X + _scroll) : -1;

        // A highlighted stretch goes whole so we can define names of phrases. 'River-wood' can be looked up like this, or anything else.
        var (from, to) = _selectionLastFrame;
        if (index >= from && index < to && SpellCheck.Trimmed(text, from, Math.Min(to, text.Length)) is var (at, size))
        {
            OpenMenuOn(text, at, size, misspelled: _lastMarks.Contains((at, size)));
            return;
        }

        foreach (var (left, right, top, bottom, start, length) in _drawn)
            if (Inside(mouse, left, top, right, bottom))
            {
                OpenMenuOn(text, start, length, misspelled: true);
                return;
            }

        if (index >= 0 && speller()?.WordAt(text, index) is var (word, wordLength))
            OpenMenuOn(text, word, wordLength, misspelled: false);
        else
            _pendingWord = string.Empty;
    }

    private void WatchForTrigger(string text, int cursor)
    {
        // Only while typing, because out of the box Insert can be one of the game's binds.
        if (!PressedNow(Down(Insert) && RaptureAtkModule.Instance()->AtkModule.IsTextInputActive(), ref _triggerHeld))
            return;

        var caret = Math.Clamp(cursor, 0, text.Length);
        var (start, length, misspelled) = (-1, 0, true);

        // For the purposes of accessibility, 'just past a word' is the same click as 'on it'.
        foreach (var mark in _lastMarks)
            if (caret >= mark.Start && caret <= mark.Start + mark.Length)
            {
                (start, length) = mark;
                break;
            }

        if (start < 0 && (speller()?.WordAt(text, caret) ?? speller()?.WordAt(text, caret - 1)) is var (at, size))
            (start, length, misspelled) = (at, size, false);

        if (OpenMenuOn(text, start, length, misspelled))
            _menuAt = new Vector2(_rect.X + Width(text, start) - _scroll, _rect.Y + _rect.H);
    }

    private int IndexAt(string text, float x)
    {
        var (low, high) = (0, text.Length);

        while (low < high)
        {
            var mid = (low + high + 1) / 2;

            if (Width(text, mid) <= x)
                low = mid;
            else
                high = mid - 1;
        }

        return low;
    }

    private static bool PressedNow(bool down, ref bool held)
    {
        var pressed = down && !held;
        held = down;
        return pressed;
    }

    private bool OpenMenuOn(string text, int start, int length, bool misspelled)
    {
        if (start < 0 || start + length > text.Length)
            return false;

        var word = text.Substring(start, length);
        (_pendingWord, _pendingMisspelled, _menuShowing, _menuAt) = (word, misspelled, true, PointerPosition());
        (_menuId, _replace) = ($"game {++_menusOpened}", replacement => Replace(word, replacement, start));
        _caretAtOpen = _caretLastFrame;
        _clickHeld = Down(LeftButton) || Down(RightButton);
        return true;
    }

    internal void Draw()
    {
        _menuWanted = false;

        // Windows answers for the whole machine, so every key and click check is gated on this.
        // This reminds me I'm going to need to get someone on Linux to make sure Wine handles this the same...
        if (_off || speller() == null || !Dalamud.Utility.Util.ApplicationIsActivated())
            return;

        try
        {
            _menuWanted = MenuWanted(Mark());

            // Only a clean frame clears it, so it takes FailuresAllowed in a row.
            _failures = 0;
        }
        catch (Exception ex)
        {
            _failures++;

            if (_failures < FailuresAllowed)
            {
                Svc.Log.Warning($"Marking the game's chat box failed ({_failures}): {ex.Message}");
                return;
            }

            _off = true;
            Svc.Log.Error(ex, "Marking the game's chat box failed repeatedly; it is now off.");
            Svc.Chat.Print(
                "[TildeTools] Spellchecking the game's chat box has switched itself off after " +
                "repeated errors. Reload the plugin to try again; /xllog has the detail.");
        }
    }

    // The box's text, empty when there's nothing to mark.
    private string Mark()
    {
        if (AtkTextNode.MemberFunctionPointers.GetTextDrawSize == null)
        {
            _off = true;
            Svc.Log.Warning("The game's text measurement could not be found, so the game's own chat box will not be marked. Everything else is unaffected.");
            return string.Empty;
        }

        var input = MeasurableInput();
        if (input == null)
            return string.Empty;

        var text = TextOf(input);
        if (text.Length == 0)
            return text;

        // The box holds the game's own codes at times: a color code when Tab's auto-translate opens over the menu.
        // Their bytes get eaten by the decode, and GetTextDrawSize reads a broken one's length far past the line.
        if (ChannelCommands.HasPayload(text))
            return text;

        _node = input->AtkTextNode;
        _rect = ScreenRect(&_node->AtkResNode);

        var measuring = (text, _node->FontSize, _node->AlignmentFontType, _rect.W, _rect.H);
        if (measuring != _widthsFor)
        {
            _widthsFor = measuring;
            _widths.Clear();
        }

        _scroll = ScrollOffset(input->CursorContainer, text, input->CursorPos);
        Underline(text, Check(text));

        WatchForTrigger(text, input->CursorPos);
        WatchForRightClick(text);
        _caretLastFrame = input->CursorPos;
        _selectionLastFrame = Selection(input);
        return text;
    }

    // The focused box keeps its own copy, as in Replace.
    private static (int Start, int End) Selection(AtkComponentTextInput* input)
    {
        var module = RaptureAtkModule.Instance();
        var (from, to) = module->IsTextInputActive() ? (module->TextInput.SelectionStart, module->TextInput.SelectionEnd) : (input->SelectionStart, input->SelectionEnd);
        return from == to ? (-1, -1) : (Math.Min(from, to), Math.Max(from, to));
    }

    // Only good until Mark returns.
    private AtkTextNode* _node;
    private (float X, float Y, float W, float H) _rect;
    private float _scroll;

    // The box holds up to UnlockedMaxBytes, which is way too many to decode every frame.
    private string TextOf(AtkComponentTextInput* input)
    {
        var raw = input->RawString.AsSpan();
        if (!raw.SequenceEqual(_rawSeen))
            (_rawSeen, _textSeen) = (raw.ToArray(), input->RawString.ToString());

        return _textSeen;
    }

    private AtkComponentTextInput* MeasurableInput()
    {
        var chatLog = Svc.GameGui.GetAddonByName("ChatLog");
        if (!chatLog.IsVisible || Svc.GameGui.GameUiHidden)
            return null;

        var input = ((AddonChatLog*)chatLog.Address)->TextInput;
        return input == null || input->AtkTextNode == null || input->CursorContainer == null ? null : input;
    }

    // Put on the background draw list so a plugin window over the chat box covers the marks.
    private void Underline(string text, List<(int Start, int Length)> misspellings)
    {
        _drawn.Clear();

        var (x, top, w, h) = _rect;
        var drawList = ImGui.GetBackgroundDrawList();
        var y = top + h - Drop;

        for (var i = FirstShowing(text, misspellings); i < misspellings.Count; i++)
        {
            var (start, length) = misspellings[i];
            var left = x + Width(text, start) - _scroll;
            if (left > x + w)
                break;

            (left, var right) = (Math.Max(left, x), Math.Min(x + Width(text, start + length) - _scroll, x + w));

            drawList.AddLine(new Vector2(left, y), new Vector2(right, y), Color, Thickness);

            _drawn.Add((left, right, top, top + h, start, length));
        }
    }

    // SpellIpc.Marks come in text order, so their widths do as well.
    private int FirstShowing(string text, List<(int Start, int Length)> marks)
    {
        var (low, high) = (0, marks.Count);

        while (low < high)
        {
            var mid = (low + high) / 2;
            if (Width(text, marks[mid].Start + marks[mid].Length) < _scroll)
                low = mid + 1;
            else
                high = mid;
        }

        return low;
    }

    private Vector2 _menuAt;
    private Vector2 _menuSize;
    private bool _menuShowing;
    private bool _clickHeld;

    private bool _menuWanted;

    private bool MenuWanted(string text)
    {
        // Tab's auto-translate puts a code in the line, see Mark
        // Replace can't rewrite a line holding one, so the menu closes instead of crashing, which is what it did previously.
        if (text.Length == 0 || !text.Contains(_pendingWord, StringComparison.Ordinal) || (_menuShowing && ChannelCommands.HasPayload(text)))
        {
            _menuShowing = false;
            _pendingWord = string.Empty;
            return false;
        }

        return _menuShowing && _pendingWord.Length > 0;
    }

    // Size is last frame's, so the first frame can be off since.
    private Vector2 MenuPosition()
    {
        var screen = ImGui.GetIO().DisplaySize;
        var at = _menuAt;

        if (_menuSize.Y > 0 && at.Y + _menuSize.Y > screen.Y)
            at.Y = Math.Max(0, at.Y - _menuSize.Y);

        if (_menuSize.X > 0 && at.X + _menuSize.X > screen.X)
            at.X = Math.Max(0, screen.X - _menuSize.X);

        return at;
    }

    // A window rather than an ImGui popup, because popups close themselves off the partial mouse input here; it only half-sees the mouse and promptly closes on the click that opened it.
    // I hate this game's native UI elements, man
    private sealed class MenuWindow : Window
    {
        private readonly NativeChatSpelling _owner;

        internal MenuWindow(NativeChatSpelling owner)
            : base("##tildetools-native-spelling",
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav)
        {
            _owner = owner;

            (RespectCloseHotkey, DisableWindowSounds, DisableFadeInFadeOut) = (false, true, true);
        }

        public override void PreOpenCheck() => IsOpen = _owner._menuWanted;

        public override void PreDraw() => ImGui.SetNextWindowPos(_owner.MenuPosition(), ImGuiCond.Always);

        public override void Draw()
        {
            _owner.DrawMenuItems();

            var min = ImGui.GetWindowPos();
            _owner._menuSize = ImGui.GetWindowSize();
            _owner.CloseOnEscapeOrClickAway(min, min + _owner._menuSize);
        }
    }

    internal Window Menu => field ??= new MenuWindow(this);

    private void DrawMenuItems()
    {
        if (speller()?.Menu.Draw(_menuId, _pendingWord, _pendingMisspelled, _replace) == true)
            Forget();
    }

    private void CloseOnEscapeOrClickAway(Vector2 min, Vector2 max)
    {
        // New presses only, or the right-click that opened it closes it when the menu flips above the pointer.
        var pressed = PressedNow(Down(LeftButton) || Down(RightButton), ref _clickHeld);

        if (Down(Escape) || (pressed && !Inside(PointerPosition(), min.X, min.Y, max.X, max.Y)))
            Forget();
    }

    private void Forget() => (_menuShowing, _pendingWord) = (false, string.Empty);

    // Redows the marks and only the marks, doesn't call Forget so a name learned with the menu open doesn't close it.
    internal void Recheck() => _lastText = string.Empty;

    // Rewrites the whole line because the component can't replace a range.
    // Read fresh, since the text the menu opened on would put back anything deleted after.
    // I HATE this game's native UI elements!
    private void Replace(string word, string replacement, int at)
    {
        var input = ChatSender.ChatLogInput();
        if (input == null)
            return;

        var text = TextOf(input);

        // A code's bytes don't survive the decode, so SetText would write it back broken.
        if (ChannelCommands.HasPayload(text))
            return;

        if (at < 0 || at + word.Length > text.Length || string.CompareOrdinal(text, at, word, 0, word.Length) != 0)
            for (at = text.IndexOf(word, StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.Ordinal))
                if ((at == 0 || !char.IsLetterOrDigit(text[at - 1])) && (at + word.Length >= text.Length || !char.IsLetterOrDigit(text[at + word.Length])))
                    break;

        if (at < 0)
            return;

        var line = text[..at] + replacement + text[(at + word.Length)..];

        // Where it was before the menu, moved by the length change when it was past the word
        var caret = _caretAtOpen < 0 || _caretAtOpen > text.Length ? line.Length
            : _caretAtOpen <= at ? _caretAtOpen
            : _caretAtOpen >= at + word.Length ? _caretAtOpen + replacement.Length - word.Length
            : at + replacement.Length;

        input->SetText(line);
        input->CursorPos = input->SelectionStart = input->SelectionEnd = caret;

        // I HATE THIS GAME'S NATIVE UI ELEMENTS I HATE THIS GAME'S NATIVE UI ELEMENTS I HATE THIS GAME'S NATIVE UI ELEMENTS I HATE THIS GAME'S NATIVE UI ELEMENTS
        // The focused box keeps its own copy, split at the caret, and that copy wins over the component's.
        // Its caret, selection and length too, or the next key lands at the old caret and gets lost.
        // I despise this but I can't think of a better way to do it and it Just Works™, so screw it.
        var module = RaptureAtkModule.Instance();
        if (!module->IsTextInputActive())
            return;

        var active = &module->TextInput;
        var (before, after) = (line[..caret], line[caret..]);
        active->RawTextBeforeSelection.SetString(before);
        active->RawSelectedText.SetString("");
        active->RawTextAfterSelection.SetString(after);
        active->RawInputString.SetString(line);
        active->EvaluatedTextBeforeSelection.SetString(before);
        active->EvaluatedSelectedText.SetString("");
        active->EvaluatedTextAfterSelection.SetString(after);
        active->EvaluatedInputString.SetString(line);
        active->CursorPos = active->SelectionStart = active->SelectionEnd = (short)caret;
        active->TextLength = (short)line.Length;
    }

    // Nothing exposes it, so it's the gap between the measured prefix and the caret.
    // CursorPos specifically counts characters, not bytes.
    private float ScrollOffset(AtkResNode* caret, string text, int cursor) =>
        Math.Max(0f, Width(text, Math.Min(cursor, text.Length)) - (ScreenRect(caret).X - _rect.X));

    // GetTextDrawSize's width is a ushort, and 32000 characters run past 200,000 px
    // So a piece is...at most:
    // 2 * PieceChars - 1 = 511 characters, under 65,535 px while glyphs are under 128 px
    private const int PieceChars = 256;

    private float Width(string text, int length)
    {
        if (length <= 0)
            return 0f;

        if (_widths.TryGetValue(length, out var kept))
            return kept;

        var from = (length - 1) / PieceChars * PieceChars;
        if (from > 0 && text.LastIndexOf(' ', from - 1, PieceChars) is var space and >= 0)
            from = space + 1;

        return _widths[length] = Width(text, from) + Measure(text, from, length - from);
    }

    // Its own buffer rather than a range in the line, because the range form's units shift marks after multi-byte characters.
    // Fits on the stack since a piece is at most 511 characters, which GetMaxByteCount puts at 1,536 bytes.
    private float Measure(string text, int start, int count)
    {
        var capacity = Encoding.UTF8.GetMaxByteCount(count);
        var bytes = stackalloc byte[capacity];
        var length = Encoding.UTF8.GetBytes(text.AsSpan(start, count), new Span<byte>(bytes, capacity));

        ushort width = 0, height = 0;
        _node->GetTextDrawSize(&width, &height, bytes, 0, length, true);
        return width;
    }

    private static (float X, float Y, float W, float H) ScreenRect(AtkResNode* node)
    {
        var (x, y, scaleX, scaleY) = (0f, 0f, 1f, 1f);

        for (var n = node; n != null; n = n->ParentNode)
        {
            x = (x * n->ScaleX) + n->X;
            y = (y * n->ScaleY) + n->Y;
            scaleX *= n->ScaleX;
            scaleY *= n->ScaleY;
        }

        return (x, y, node->Width * scaleX, node->Height * scaleY);
    }

    private List<(int Start, int Length)> Check(string text)
    {
        if (text != _lastText)
            (_lastText, _lastMarks) = (text, speller()?.Marks(text) ?? []);

        return _lastMarks;
    }
}
