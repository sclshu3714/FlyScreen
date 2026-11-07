#if ANDROID
using Android.Content;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using AndroidX.AppCompat.Widget;
using Microsoft.Maui.Platform;
using AClipboard = global::Android.Content.ClipboardManager;
using AMenu = global::Android.Views.IMenu;
using AMenuItem = global::Android.Views.IMenuItem;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// 系统输入法载体：拼音只留在本地；上屏文字、退格、回车、方向键和剪贴板命令发给对端。
/// 本地框在上屏后会被清空，输入法会因此禁用「向左」并拒绝复制/剪切，这里用一段不显示的占位文本把这些操作骗出来。
/// </summary>
public sealed class PhoneRemoteEditText : MauiAppCompatEditText
{
    internal const char PasteChar = '\uE000';
    internal const char PadChar = '\uE001';
    internal const int FakeLen = 64;
    internal const int FakeMid = 32;

    internal enum EditSource { Commit, DeleteSurrounding, KeyEvent, EditorAction, Selection, Clipboard }

    private PhoneImeEntry? _entry;
    private bool _clearing;
    private ushort _lastVk;
    private EditSource _lastSource;
    private long _lastMs;
    private ushort _chordVk;
    private long _chordMs;
    private int _selGen;
    private int _track = FakeMid;
    private long _navMs;
    private bool _imeCtrl;
    private long _imeCtrlMs;
    private bool _imeShift;
    private long _imeShiftMs;
    private Keycode _downCode;
    private long _downMs;
    private Keycode _upEcho;
    private long _upEchoMs;
    private bool _watchingClip;
    private bool _seededClip;
    private long _ignoreClipUntil;
    private Java.Lang.String? _padBuffer;
    private Java.Lang.String? _onePad;
    private Java.Lang.String? _pasteMark;
    private ClipWatcher? _clipWatcher;

    public PhoneRemoteEditText(Context context) : base(context)
    {
        ShowSoftInputOnFocus = true;
        SetSingleLine(true);
        InputType = InputTypes.ClassText;
        ImeOptions = RemoteImeOptions();
        Focusable = true;
        FocusableInTouchMode = true;
    }

    internal bool RemoteSelection { get; private set; }

    internal void Attach(PhoneImeEntry? entry) => _entry = entry;

    internal void InstallEditMenu()
    {
        var menu = new EditMenuCallback(this);
        CustomInsertionActionModeCallback = menu;
        CustomSelectionActionModeCallback = menu;
    }

    internal static ImeAction RemoteImeOptions()
        => (ImeAction)((int)ImeAction.None
            | (int)ImeFlags.NoEnterAction
            | (int)ImeFlags.NoExtractUi
            | (int)ImeFlags.NoFullscreen);

    internal static bool IsPasteMark(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in text)
            if (c != PasteChar) return false;
        return true;
    }

    internal static bool IsRemoteOnlyText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in text)
            if (c != PasteChar && c != PadChar) return false;
        return true;
    }

    internal static string StripPad(string text)
        => text.Replace(PadChar.ToString(), "");

    public override IInputConnection? OnCreateInputConnection(EditorInfo? outAttrs)
    {
        var connection = base.OnCreateInputConnection(outAttrs);
        if (outAttrs != null)
        {
            outAttrs.InputType = InputTypes.ClassText;
            outAttrs.ImeOptions = (ImeFlags)(int)RemoteImeOptions();
        }

        EnsurePasteTarget();
        return connection == null ? null : new PhoneRemoteInputConnection(connection, true, this);
    }

    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        TryWatchClipboard();
    }

    protected override void OnDetachedFromWindow()
    {
        ClearSeededClip();
        UnwatchClipboard();
        base.OnDetachedFromWindow();
    }

    public override void OnWindowFocusChanged(bool hasWindowFocus)
    {
        base.OnWindowFocusChanged(hasWindowFocus);
        if (hasWindowFocus)
            EnsurePasteTarget();
    }

    protected override void OnFocusChanged(bool gainFocus, FocusSearchDirection direction, global::Android.Graphics.Rect? previouslyFocusedRect)
    {
        base.OnFocusChanged(gainFocus, direction, previouslyFocusedRect);
        if (!gainFocus)
            ClearSeededClip();
        else
            EnsurePasteTarget();
    }

    public override bool OnTextContextMenuItem(int id)
    {
        if (DispatchMenu(id)) return true;
        return base.OnTextContextMenuItem(id);
    }

    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e != null && !IsComposing && IsEditorKey(e.KeyCode, CtrlPressed(e)) && HandleEditorKey(e))
            return true;
        return base.DispatchKeyEvent(e);
    }

    internal bool DispatchMenu(int id)
    {
        ushort vk = id switch
        {
            global::Android.Resource.Id.SelectAll => 0x41,
            global::Android.Resource.Id.Copy => 0x43,
            global::Android.Resource.Id.Cut => 0x58,
            global::Android.Resource.Id.Paste => 0x56,
            global::Android.Resource.Id.PasteAsPlainText => 0x56,
            _ => 0
        };
        if (vk == 0) return false;
        EmitKey(vk, extended: false, shift: false, ctrl: true, EditSource.Clipboard);
        return true;
    }

    internal bool IsComposing
    {
        get
        {
            if (EditableText is not ISpannable span) return false;
            return BaseInputConnection.GetComposingSpanStart(span) >= 0;
        }
    }

    internal void ClearCommitted()
    {
        if (_clearing) return;
        var editable = EditableText;
        if (editable == null || editable.Length() == 0) return;
        _clearing = true;
        try
        {
            editable.Delete(0, editable.Length());
        }
        finally
        {
            _clearing = false;
        }
    }

    internal void EmitText(string text)
    {
        if (text.Length == 0) return;
        _selGen++;
        RemoteSelection = false;
        if (text == " " && ShouldSkipDuplicate(0x20, EditSource.Commit)) return;
        _entry?.EmitText(text);
    }

    internal void EmitKey(ushort vk, bool extended, bool shift, bool ctrl, EditSource source)
    {
        _selGen++;
        if (_entry == null) return;
        if (IsRepeatChord(vk, ctrl)) return;
        if (ShouldSkipDuplicate(vk, source)) return;
        ApplyNavState(vk, shift, ctrl);
        _entry.EmitKey(vk, extended, shift, ctrl);
    }

    internal void EmitBackspaces(int before, int after)
    {
        for (var i = 0; i < before; i++)
            EmitKey(0x08, extended: false, shift: false, ctrl: false, EditSource.DeleteSurrounding);
        for (var i = 0; i < after; i++)
            EmitKey(0x2E, extended: true, shift: false, ctrl: false, EditSource.DeleteSurrounding);
    }

    internal Java.Lang.ICharSequence OnePad()
        => _onePad ??= new Java.Lang.String(PadChar.ToString());

    internal Java.Lang.ICharSequence PasteMarkText()
        => _pasteMark ??= new Java.Lang.String(PasteChar.ToString());

    internal ExtractedText CurrentExtract()
    {
        _padBuffer ??= new Java.Lang.String(new string(PadChar, FakeLen));
        return new ExtractedText
        {
            Text = _padBuffer,
            StartOffset = 0,
            SelectionStart = RemoteSelection ? FakeMid - 1 : FakeMid,
            SelectionEnd = FakeMid,
            PartialStartOffset = -1,
            PartialEndOffset = -1
        };
    }

    /// <summary>
    /// 输入法面板的「向左」会先问光标前有没有字。本地框是空的，所以这里让选择落在占位文本中间。
    /// 若同一次点击已经发过方向键，延迟一拍再决定，避免和按键各走一步。
    /// </summary>
    internal void ApplySelection(int start, int end)
    {
        // 空编辑框会把光标同步成 0,0，不能把它当成「移到行首」，也不能取消刚才那一次左移。
        if (start == 0 && end == 0) return;
        var gen = ++_selGen;
        if (start <= 0 && end >= FakeLen - 1)
        {
            EmitKey(0x41, extended: false, shift: false, ctrl: true, EditSource.Selection);
            return;
        }

        PostDelayed(() =>
        {
            if (gen != _selGen || _entry == null) return;
            if (SystemClock.UptimeMillis() - _navMs < 48) return;
            CommitSelection(start, end);
        }, 24);
    }

    internal bool HandleEditorKey(KeyEvent e)
    {
        var code = e.KeyCode;
        if (code is Keycode.CtrlLeft or Keycode.CtrlRight)
        {
            _imeCtrl = e.Action == KeyEventActions.Down;
            if (_imeCtrl) _imeCtrlMs = SystemClock.UptimeMillis();
            return true;
        }

        if (code is Keycode.ShiftLeft or Keycode.ShiftRight)
        {
            _imeShift = e.Action == KeyEventActions.Down;
            if (_imeShift) _imeShiftMs = SystemClock.UptimeMillis();
            return true;
        }

        if (IsComposing && code is Keycode.Del or Keycode.ForwardDel or Keycode.Enter or Keycode.NumpadEnter or Keycode.Space)
            return false;

        var ctrl = CtrlPressed(e);
        var shift = ShiftPressed(e);
        if (!IsEditorKey(code, ctrl)) return false;

        if (e.Action == KeyEventActions.Up)
        {
            if (!ShouldEmitUp(code)) return true;
        }
        else if (e.Action != KeyEventActions.Down)
        {
            return true;
        }
        else
        {
            MarkDown(code);
        }

        if (ctrl && !shift && TryCtrlChord(code, out var chord))
        {
            EmitKey(chord, extended: false, shift: false, ctrl: true, EditSource.KeyEvent);
            return true;
        }

        if (TryClipboardKey(code, out var clip))
        {
            EmitKey(clip, extended: false, shift: false, ctrl: true, EditSource.KeyEvent);
            return true;
        }

        if (!TryMap(code, out var vk, out var extended))
            return true;

        EmitKey(vk, extended, shift, ctrl, EditSource.KeyEvent);
        return true;
    }

    internal bool TryPrivateCommand(string? action)
    {
        if (string.IsNullOrEmpty(action)) return false;
        var a = action.ToLowerInvariant();
        if (a.Contains("selectall") || a.Contains("select_all") || a.Contains("select-all"))
            return DispatchMenu(global::Android.Resource.Id.SelectAll);
        if (a.Contains("paste"))
            return DispatchMenu(global::Android.Resource.Id.Paste);
        if (a.Contains("cut"))
            return DispatchMenu(global::Android.Resource.Id.Cut);
        if (a.Contains("copy"))
            return DispatchMenu(global::Android.Resource.Id.Copy);
        return false;
    }

    private void CommitSelection(int start, int end)
    {
        if (start == end)
        {
            EmitSteps(DeltaToward(start), shift: false);
            _track = Math.Clamp(start, 0, FakeLen);
            return;
        }

        var delta = end - _track;
        if (end == FakeMid - 1) delta = -1;
        else if (end == FakeMid + 1) delta = 1;
        if (Math.Abs(delta) > 8) delta = 0;
        EmitSteps(delta, shift: true);
        _track = Math.Clamp(end, 0, FakeLen);
        RemoteSelection = true;
    }

    private int DeltaToward(int caret)
    {
        if (caret == FakeMid - 1) return -1;
        if (caret == FakeMid + 1) return 1;
        var delta = caret - _track;
        return Math.Abs(delta) > 8 ? 0 : delta;
    }

    private void EmitSteps(int delta, bool shift)
    {
        if (delta == 0) return;
        var vk = delta < 0 ? (ushort)0x25 : (ushort)0x27;
        EmitKey(vk, extended: true, shift, ctrl: false, EditSource.Selection);
    }

    private void ApplyNavState(ushort vk, bool shift, bool ctrl)
    {
        if (ctrl && vk == 0x41)
        {
            RemoteSelection = true;
            return;
        }

        if (ctrl && vk is 0x43 or 0x58 or 0x56)
        {
            if (vk != 0x43)
                RemoteSelection = false;
            return;
        }

        if (vk is 0x08 or 0x2E or 0x0D)
        {
            RemoteSelection = false;
            return;
        }

        if (vk is not (0x25 or 0x26 or 0x27 or 0x28 or 0x24 or 0x23))
            return;

        _navMs = SystemClock.UptimeMillis();
        RemoteSelection = shift;
        if (vk == 0x25) _track = Math.Max(0, _track - 1);
        else if (vk == 0x27) _track = Math.Min(FakeLen, _track + 1);
    }

    private bool IsRepeatChord(ushort vk, bool ctrl)
    {
        if (!ctrl || vk is not (0x41 or 0x43 or 0x58 or 0x56)) return false;
        var now = SystemClock.UptimeMillis();
        if (vk == _chordVk && now - _chordMs < 200) return true;
        _chordVk = vk;
        _chordMs = now;
        return false;
    }

    /// <summary>同一按键若在极短时间内从两条输入法回调进来，只发一次。方向键只合并同一次点击的双发，不吞连发。</summary>
    private bool ShouldSkipDuplicate(ushort vk, EditSource source)
    {
        var now = SystemClock.UptimeMillis();
        var nav = vk is 0x25 or 0x26 or 0x27 or 0x28 or 0x24 or 0x23;
        var window = nav ? 16 : 45;
        var skip = vk == _lastVk && (nav || source != _lastSource) && now - _lastMs < window;
        if (!skip)
        {
            _lastVk = vk;
            _lastSource = source;
            _lastMs = now;
        }

        return skip;
    }

    private bool CtrlPressed(KeyEvent e)
    {
        if (e.IsCtrlPressed) return true;
        if (!_imeCtrl) return false;
        if (SystemClock.UptimeMillis() - _imeCtrlMs > 800)
        {
            _imeCtrl = false;
            return false;
        }

        return true;
    }

    private bool ShiftPressed(KeyEvent e)
    {
        if (e.IsShiftPressed) return true;
        if (!_imeShift) return false;
        if (SystemClock.UptimeMillis() - _imeShiftMs > 800)
        {
            _imeShift = false;
            return false;
        }

        return true;
    }

    private void MarkDown(Keycode code)
    {
        _downCode = code;
        _downMs = SystemClock.UptimeMillis();
    }

    private bool ShouldEmitUp(Keycode code)
    {
        var now = SystemClock.UptimeMillis();
        if (_downCode == code && now - _downMs < 500)
        {
            _downCode = Keycode.Unknown;
            _upEcho = code;
            _upEchoMs = now;
            return false;
        }

        if (_upEcho == code && now - _upEchoMs < 500)
            return false;

        _upEcho = code;
        _upEchoMs = now;
        return true;
    }

    private static bool IsEditorKey(Keycode code, bool ctrl)
    {
        if (code is Keycode.A or Keycode.C or Keycode.X or Keycode.V)
            return ctrl;
        if (TryClipboardKey(code, out _)) return true;
        return code is Keycode.Del or Keycode.ForwardDel or Keycode.Enter or Keycode.NumpadEnter
            or Keycode.DpadLeft or Keycode.DpadUp or Keycode.DpadRight or Keycode.DpadDown
            or Keycode.MoveHome or Keycode.MoveEnd or Keycode.Tab or Keycode.Space;
    }

    private static bool TryCtrlChord(Keycode code, out ushort vk)
    {
        vk = code switch
        {
            Keycode.A => 0x41,
            Keycode.C => 0x43,
            Keycode.X => 0x58,
            Keycode.V => 0x56,
            _ => 0
        };
        return vk != 0;
    }

    private static bool TryClipboardKey(Keycode code, out ushort vk)
    {
        // KEYCODE_CUT=277 KEYCODE_COPY=278 KEYCODE_PASTE=279（API 24）
        vk = (int)code switch
        {
            277 => 0x58,
            278 => 0x43,
            279 => 0x56,
            _ => 0
        };
        return vk != 0;
    }

    private static bool TryMap(Keycode code, out ushort vk, out bool extended)
    {
        extended = false;
        switch (code)
        {
            case Keycode.Del:
                vk = 0x08;
                return true;
            case Keycode.ForwardDel:
                vk = 0x2E;
                extended = true;
                return true;
            case Keycode.Enter:
            case Keycode.NumpadEnter:
                vk = 0x0D;
                return true;
            case Keycode.DpadLeft:
                vk = 0x25;
                extended = true;
                return true;
            case Keycode.DpadUp:
                vk = 0x26;
                extended = true;
                return true;
            case Keycode.DpadRight:
                vk = 0x27;
                extended = true;
                return true;
            case Keycode.DpadDown:
                vk = 0x28;
                extended = true;
                return true;
            case Keycode.MoveHome:
                vk = 0x24;
                extended = true;
                return true;
            case Keycode.MoveEnd:
                vk = 0x23;
                extended = true;
                return true;
            case Keycode.Tab:
                vk = 0x09;
                return true;
            case Keycode.Space:
                vk = 0x20;
                return true;
            default:
                vk = 0;
                return false;
        }
    }

    private AClipboard? Clipboard()
        => Context?.GetSystemService(Context.ClipboardService) as AClipboard;

    private void TryWatchClipboard()
    {
        if (_watchingClip) return;
        try
        {
            var cm = Clipboard();
            if (cm == null) return;
            _clipWatcher ??= new ClipWatcher(this);
            cm.AddPrimaryClipChangedListener(_clipWatcher);
            _watchingClip = true;
        }
        catch
        {
            /* 没有剪贴板服务时，粘贴仍走按键和菜单 */
        }
    }

    private void UnwatchClipboard()
    {
        if (!_watchingClip) return;
        try
        {
            if (_clipWatcher != null)
                Clipboard()?.RemovePrimaryClipChangedListener(_clipWatcher);
        }
        catch
        {
            /* ignore */
        }

        _watchingClip = false;
    }

    /// <summary>手机剪贴板是空的时，文字编辑里的「粘贴」会被输入法直接禁用。</summary>
    private void EnsurePasteTarget()
    {
        try
        {
            var cm = Clipboard();
            if (cm == null) return;
            var text = cm.PrimaryClip?.GetItemAt(0)?.Text;
            if (!string.IsNullOrEmpty(text) && !IsPasteMark(text)) return;
            if (IsPasteMark(text))
            {
                _seededClip = true;
                return;
            }

            _ignoreClipUntil = SystemClock.UptimeMillis() + 600;
            cm.PrimaryClip = global::Android.Content.ClipData.NewPlainText("text", PasteChar.ToString());
            _seededClip = true;
        }
        catch
        {
            /* 读不到剪贴板时不覆盖用户内容 */
        }
    }

    private void ClearSeededClip()
    {
        if (!_seededClip) return;
        _seededClip = false;
        try
        {
            var cm = Clipboard();
            if (cm == null) return;
            var text = cm.PrimaryClip?.GetItemAt(0)?.Text;
            if (!IsPasteMark(text)) return;
            _ignoreClipUntil = SystemClock.UptimeMillis() + 600;
            if (OperatingSystem.IsAndroidVersionAtLeast(28))
                cm.ClearPrimaryClip();
            else
                cm.PrimaryClip = global::Android.Content.ClipData.NewPlainText("text", "");
        }
        catch
        {
            /* ignore */
        }
    }

    internal void OnPhoneClipChanged()
    {
        if (SystemClock.UptimeMillis() < _ignoreClipUntil || !RemoteSelection) return;
        try
        {
            var text = Clipboard()?.PrimaryClip?.GetItemAt(0)?.Text;
            if (!IsPasteMark(text)) return;
            EmitKey(0x43, extended: false, shift: false, ctrl: true, EditSource.Clipboard);
        }
        catch
        {
            /* ignore */
        }
    }

    private sealed class ClipWatcher : Java.Lang.Object, AClipboard.IOnPrimaryClipChangedListener
    {
        private readonly PhoneRemoteEditText _owner;
        public ClipWatcher(PhoneRemoteEditText owner) => _owner = owner;
        public void OnPrimaryClipChanged() => _owner.OnPhoneClipChanged();
    }

    private sealed class EditMenuCallback : Java.Lang.Object, ActionMode.ICallback
    {
        private readonly PhoneRemoteEditText _owner;
        public EditMenuCallback(PhoneRemoteEditText owner) => _owner = owner;

        public bool OnCreateActionMode(ActionMode? mode, AMenu? menu)
        {
            if (menu == null) return false;
            menu.Clear();
            menu.Add(0, global::Android.Resource.Id.SelectAll, 0, "全选");
            menu.Add(0, global::Android.Resource.Id.Cut, 1, "剪切");
            menu.Add(0, global::Android.Resource.Id.Copy, 2, "复制");
            menu.Add(0, global::Android.Resource.Id.Paste, 3, "粘贴");
            return true;
        }

        public bool OnPrepareActionMode(ActionMode? mode, AMenu? menu) => false;

        public bool OnActionItemClicked(ActionMode? mode, AMenuItem? item)
        {
            if (item != null)
                _owner.DispatchMenu(item.ItemId);
            mode?.Finish();
            return true;
        }

        public void OnDestroyActionMode(ActionMode? mode) { }
    }
}

internal sealed class PhoneRemoteInputConnection : InputConnectionWrapper
{
    private readonly PhoneRemoteEditText _edit;

    public PhoneRemoteInputConnection(IInputConnection target, bool mutable, PhoneRemoteEditText edit)
        : base(target, mutable)
    {
        _edit = edit;
    }

    public override bool CommitText(Java.Lang.ICharSequence? text, int newCursorPosition)
    {
        var value = text?.ToString() ?? "";
        if (value.Length == 0)
            return base.CommitText(text, newCursorPosition);

        if (PhoneRemoteEditText.IsPasteMark(value))
        {
            _edit.EmitKey(0x56, extended: false, shift: false, ctrl: true, PhoneRemoteEditText.EditSource.Commit);
            return true;
        }

        value = PhoneRemoteEditText.StripPad(value);
        if (value.Length == 0)
            return true;

        if (value is "\n" or "\r" or "\r\n")
        {
            if (!_edit.IsComposing)
                _edit.EmitKey(0x0D, extended: false, shift: false, ctrl: false, PhoneRemoteEditText.EditSource.Commit);
            return true;
        }

        var ok = base.CommitText(new Java.Lang.String(value), newCursorPosition);
        _edit.EmitText(value);
        if (!_edit.IsComposing)
            _edit.ClearCommitted();
        return ok;
    }

    public override bool SetComposingText(Java.Lang.ICharSequence? text, int newCursorPosition)
        => base.SetComposingText(text, newCursorPosition);

    public override bool DeleteSurroundingText(int beforeLength, int afterLength)
        => DeleteUnits(beforeLength, afterLength, codePoints: false);

    public override bool DeleteSurroundingTextInCodePoints(int beforeLength, int afterLength)
        => DeleteUnits(beforeLength, afterLength, codePoints: true);

    public override bool SendKeyEvent(KeyEvent? e)
    {
        if (e == null) return base.SendKeyEvent(e);
        if (_edit.HandleEditorKey(e)) return true;
        if (e.Action != KeyEventActions.Down) return true;
        return base.SendKeyEvent(e);
    }

    public override bool PerformEditorAction(ImeAction actionCode)
    {
        if (_edit.IsComposing)
            return base.PerformEditorAction(actionCode);
        _edit.EmitKey(0x0D, extended: false, shift: false, ctrl: false, PhoneRemoteEditText.EditSource.EditorAction);
        return true;
    }

    public override bool PerformContextMenuAction(int id)
    {
        if (_edit.DispatchMenu(id)) return true;
        return base.PerformContextMenuAction(id);
    }

    public override bool PerformPrivateCommand(string? action, Bundle? data)
    {
        if (_edit.TryPrivateCommand(action)) return true;
        return base.PerformPrivateCommand(action, data);
    }

    public override Java.Lang.ICharSequence? GetTextBeforeCursorFormatted(int n, GetTextFlags flags)
    {
        if (n <= 0 || _edit.IsComposing)
            return base.GetTextBeforeCursorFormatted(n, flags);
        return _edit.OnePad();
    }

    public override Java.Lang.ICharSequence? GetTextAfterCursorFormatted(int n, GetTextFlags flags)
    {
        if (n <= 0 || _edit.IsComposing)
            return base.GetTextAfterCursorFormatted(n, flags);
        return _edit.OnePad();
    }

    public override Java.Lang.ICharSequence? GetSelectedTextFormatted(GetTextFlags flags)
    {
        if (_edit.IsComposing || !_edit.RemoteSelection)
            return base.GetSelectedTextFormatted(flags);
        return _edit.PasteMarkText();
    }

    public override ExtractedText? GetExtractedText(ExtractedTextRequest? request, GetTextFlags flags)
    {
        if (_edit.IsComposing)
            return base.GetExtractedText(request, flags);
        return _edit.CurrentExtract();
    }

    public override bool SetSelection(int start, int end)
    {
        if (_edit.IsComposing)
            return base.SetSelection(start, end);
        _edit.ApplySelection(start, end);
        return true;
    }

    public override bool RequestCursorUpdates(int cursorUpdateMode)
        => false;

    public override bool RequestCursorUpdates(int cursorUpdateMode, int cursorUpdateFilter)
        => false;

    private bool DeleteUnits(int beforeLength, int afterLength, bool codePoints)
    {
        if (beforeLength <= 0 && afterLength <= 0)
            return codePoints
                ? base.DeleteSurroundingTextInCodePoints(beforeLength, afterLength)
                : base.DeleteSurroundingText(beforeLength, afterLength);

        if (_edit.IsComposing)
            return codePoints
                ? base.DeleteSurroundingTextInCodePoints(beforeLength, afterLength)
                : base.DeleteSurroundingText(beforeLength, afterLength);

        var editable = _edit.EditableText;
        var length = editable?.Length() ?? 0;
        var start = Math.Clamp(_edit.SelectionStart, 0, length);
        var end = Math.Clamp(_edit.SelectionEnd, start, length);
        var consumeBefore = Math.Min(beforeLength, start);
        var consumeAfter = Math.Min(afterLength, length - end);
        var ok = true;
        if (consumeBefore > 0 || consumeAfter > 0)
        {
            ok = codePoints
                ? base.DeleteSurroundingTextInCodePoints(consumeBefore, consumeAfter)
                : base.DeleteSurroundingText(consumeBefore, consumeAfter);
        }

        var overflowBefore = beforeLength - consumeBefore;
        var overflowAfter = afterLength - consumeAfter;
        // 占位文本不是真的字，空框上一次最多补一个退格，避免把电脑上的一段文字清掉
        if (length == 0)
        {
            if (overflowBefore > 1) overflowBefore = 1;
            if (overflowAfter > 1) overflowAfter = 1;
        }

        _edit.EmitBackspaces(overflowBefore, overflowAfter);
        return ok;
    }
}
#endif
