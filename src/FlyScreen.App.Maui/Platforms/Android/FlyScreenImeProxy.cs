#if ANDROID
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// 电脑按键 → 手机系统输入法（可中文拼音组字）。
/// 组字中不写入目标；确认后的文本再送进原先记住的输入框。
/// 英文直出时同样经本代理提交，调用方在「无组字且无文本变化」时可回退 ACTION_SET_TEXT。
/// </summary>
internal sealed class FlyScreenImeProxy
{
    private readonly global::Android.Content.Context _context;
    private readonly Action<string> _commit;
    private readonly Action<int> _delete;
    private readonly Handler _ui = new(Looper.MainLooper!);
    private readonly Watcher _watcher;
    private IWindowManager? _wm;
    private EditText? _edit;
    private WindowManagerLayoutParams? _lp;
    private bool _added;
    private bool _updating;
    private string _sent = "";

    public FlyScreenImeProxy(global::Android.Content.Context context, Action<string> commit, Action<int> delete)
    {
        _context = context;
        _commit = commit;
        _delete = delete;
        _watcher = new Watcher(this);
    }

    public bool HasContent => !string.IsNullOrEmpty(_edit?.Text);

    public bool IsComposing
    {
        get
        {
            if (_edit?.EditableText is not ISpannable span) return false;
            return BaseInputConnection.GetComposingSpanStart(span) >= 0;
        }
    }

    public string TextSnapshot => _edit?.Text ?? "";

    public bool Ensure(IWindowManager wm, WindowManagerTypes type)
    {
        _wm = wm;
        _edit ??= CreateEdit();
        if (!_added || _lp == null || _lp.Type != type)
        {
            if (_added)
            {
                try { wm.RemoveView(_edit); } catch { /* 尚未挂上 */ }
                _added = false;
            }

            var density = _context.Resources?.DisplayMetrics?.Density ?? 2f;
            _lp = new WindowManagerLayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                (int)Math.Ceiling(36 * density),
                type,
                WindowManagerFlags.NotTouchable
                | WindowManagerFlags.NotTouchModal
                | WindowManagerFlags.LayoutInScreen
                | WindowManagerFlags.AltFocusableIm,
                global::Android.Graphics.Format.Translucent)
            {
                Gravity = GravityFlags.Bottom | GravityFlags.Left,
                // 允许系统 IME（含中文）；不用 AlwaysHidden，否则拼音引擎挂不上
                SoftInputMode = SoftInput.StateVisible | SoftInput.AdjustNothing,
                Alpha = 1f
            };
            try
            {
                wm.AddView(_edit, _lp);
                _added = true;
            }
            catch
            {
                return false;
            }
        }

        _edit.Post(FocusEditor);
        return true;
    }

    public bool Dispatch(int keyCode, bool down, bool shift)
    {
        if (_edit == null || keyCode <= 0) return false;
        if (!_edit.IsFocused)
            FocusEditor();

        var before = _edit.Text ?? "";
        var now = SystemClock.UptimeMillis();
        var meta = shift ? MetaKeyStates.ShiftOn : 0;
        var action = down ? KeyEventActions.Down : KeyEventActions.Up;
        // 虚拟软键盘设备：部分中文 IME 只认这类 KeyEvent 做拼音
        var ev = new KeyEvent(
            now, now, action, (Keycode)keyCode, 0, meta,
            deviceId: -1,
            scancode: 0,
            flags: KeyEventFlags.SoftKeyboard | KeyEventFlags.KeepTouchMode);
        var handled = _edit.DispatchKeyEvent(ev);

        // 按下后既未组字也无文本变化：用当前按键字符图补入代理框，
        // 仍走 TextWatcher→Commit（由手机 IME 状态决定；组字中不会进此分支）
        if (down && !IsComposing && string.Equals(_edit.Text ?? "", before, StringComparison.Ordinal))
        {
            try
            {
                // -1 = VIRTUAL_KEYBOARD（绑定层无同名常量）
                var map = KeyCharacterMap.Load(-1);
                var code = map.Get((Keycode)keyCode, (int)meta);
                if (code != 0 && !char.IsControl((char)code))
                {
                    _edit.EditableText?.Append(((char)code).ToString());
                    handled = true;
                }
            }
            catch
            {
                /* ignore */
            }
        }

        return handled;
    }

    public void Detach()
    {
        if (_edit != null)
        {
            try
            {
                var imm = _context.GetSystemService(global::Android.Content.Context.InputMethodService) as InputMethodManager;
                imm?.HideSoftInputFromWindow(_edit.WindowToken, HideSoftInputFlags.None);
            }
            catch { /* ignore */ }
        }

        if (_added && _wm != null && _edit != null)
        {
            try { _wm.RemoveView(_edit); } catch { /* ignore */ }
        }
        _added = false;
        _sent = "";
    }

    private EditText CreateEdit()
    {
        var edit = new EditText(_context)
        {
            ShowSoftInputOnFocus = true,
            InputType = InputTypes.ClassText,
            Alpha = 0f
        };
        edit.SetSingleLine(true);
        edit.SetBackgroundColor(global::Android.Graphics.Color.Argb(210, 255, 255, 255));
        edit.SetTextColor(global::Android.Graphics.Color.Black);
        edit.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 16);
        edit.AddTextChangedListener(_watcher);
        return edit;
    }

    private void FocusEditor()
    {
        if (_edit == null) return;
        _edit.RequestFocus();
        var imm = _context.GetSystemService(global::Android.Content.Context.InputMethodService) as InputMethodManager;
        if (imm == null) return;
        imm.RestartInput(_edit);
        // 编辑器等场景强制拉起当前系统输入法（中/英由用户手机设置决定）
        var flags = RemoteTextInputHub.HasSink ? ShowFlags.Forced : ShowFlags.Implicit;
        imm.ShowSoftInput(_edit, flags);
    }

    private void OnText(IEditable? editable)
    {
        if (_updating || _edit == null || editable == null) return;
        var text = editable.ToString() ?? "";
        var composing = editable is ISpannable span
            ? BaseInputConnection.GetComposingSpanStart(span)
            : -1;

        // 拼音组字中：只本地预览，不写入目标
        if (composing >= 0)
        {
            _edit.Alpha = 1f;
            return;
        }

        var stableEnd = text.Length;
        var stable = text[..stableEnd];
        if (stable.StartsWith(_sent, StringComparison.Ordinal))
        {
            if (stable.Length > _sent.Length)
            {
                var delta = stable[_sent.Length..];
                _sent = stable;
                if (delta.Length > 0)
                    _ui.Post(() => _commit(delta));
            }
        }
        else if (_sent.StartsWith(stable, StringComparison.Ordinal))
        {
            var removed = _sent.Length - stable.Length;
            _sent = stable;
            if (removed > 0)
                _ui.Post(() => _delete(removed));
        }
        else if (!string.IsNullOrEmpty(stable) && stable != _sent)
        {
            // 选词后整段替换
            var prev = _sent;
            _sent = stable;
            if (!string.IsNullOrEmpty(prev))
                _ui.Post(() => _delete(prev.Length));
            _ui.Post(() => _commit(stable));
        }
        else
        {
            _sent = stable;
        }

        _edit.Alpha = text.Length > 0 ? 1f : 0f;
        if (composing < 0 && text.Length > 0)
            _ui.PostDelayed(ClearEditor, 60);
    }

    private void ClearEditor()
    {
        if (_edit == null) return;
        if (_edit.EditableText is ISpannable span
            && BaseInputConnection.GetComposingSpanStart(span) >= 0)
            return;
        _updating = true;
        _sent = "";
        _edit.Text = "";
        _edit.Alpha = 0f;
        _updating = false;
        _edit.Post(FocusEditor);
    }

    private sealed class Watcher : Java.Lang.Object, ITextWatcher
    {
        private readonly FlyScreenImeProxy _owner;
        public Watcher(FlyScreenImeProxy owner) => _owner = owner;
        public void AfterTextChanged(IEditable? s) => _owner.OnText(s);
        public void BeforeTextChanged(Java.Lang.ICharSequence? s, int start, int count, int after) { }
        public void OnTextChanged(Java.Lang.ICharSequence? s, int start, int before, int count) { }
    }
}
#endif
