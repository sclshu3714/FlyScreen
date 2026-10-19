#if ANDROID
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// 把电脑按键交给手机当前输入法：先聚焦本代理框并弹出系统 IME（含中文拼音），
/// 组字中的拼音不写入目标；IME 确认后的汉字再送进原来的输入框。
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
                (int)Math.Ceiling(40 * density),
                type,
                // 可聚焦，才能把按键交给系统 IME；不抢触摸
                WindowManagerFlags.NotTouchable
                | WindowManagerFlags.NotTouchModal
                | WindowManagerFlags.LayoutInScreen
                | WindowManagerFlags.AltFocusableIm,
                global::Android.Graphics.Format.Translucent)
            {
                Gravity = GravityFlags.Bottom | GravityFlags.Left,
                // 必须允许弹出系统输入法，否则中文 IME 不会接管按键，只会变成直写英文字母
                SoftInputMode = SoftInput.StateAlwaysVisible | SoftInput.AdjustNothing,
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

        var now = SystemClock.UptimeMillis();
        var meta = shift ? MetaKeyStates.ShiftOn : 0;
        var action = down ? KeyEventActions.Down : KeyEventActions.Up;
        // deviceId=-1（虚拟键盘）+ SoftKeyboard 标志，便于拼音类 IME 走组字
        const int virtualKeyboardDevice = -1;
        var ev = new KeyEvent(
            now,
            now,
            action,
            (Keycode)keyCode,
            0,
            meta,
            virtualKeyboardDevice,
            0,
            KeyEventFlags.SoftKeyboard | KeyEventFlags.KeepTouchMode);

        // 优先经根视图派发（更接近窗口 pre-IME 路径）；失败再直接派给 EditText
        var root = _edit.RootView;
        if (root != null && !ReferenceEquals(root, _edit))
        {
            if (root.DispatchKeyEvent(ev))
                return true;
        }

        return _edit.DispatchKeyEvent(ev);
    }

    public void Detach()
    {
        HideSoftInput();
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
            // 允许弹出系统输入法（中文拼音候选）
            ShowSoftInputOnFocus = true,
            // 勿加 NoSuggestions：部分中文 IME 会因此退回直出英文
            InputType = InputTypes.ClassText,
            ImeOptions = (ImeAction)((int)ImeAction.Done | (int)ImeFlags.NoExtractUi),
            Alpha = 0.92f,
            Hint = "远程输入…"
        };
        edit.SetSingleLine(true);
        edit.SetBackgroundColor(global::Android.Graphics.Color.Argb(230, 255, 255, 255));
        edit.SetTextColor(global::Android.Graphics.Color.Black);
        edit.SetHintTextColor(global::Android.Graphics.Color.Gray);
        edit.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 15);
        edit.SetPadding(24, 8, 24, 8);
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
        // 强制拉起当前系统输入法（用户设的中文），否则按键不会进拼音引擎
        imm.ShowSoftInput(_edit, ShowFlags.Forced);
    }

    private void HideSoftInput()
    {
        if (_edit == null) return;
        var imm = _context.GetSystemService(global::Android.Content.Context.InputMethodService) as InputMethodManager;
        imm?.HideSoftInputFromWindow(_edit.WindowToken, HideSoftInputFlags.None);
    }

    private void OnText(IEditable? editable)
    {
        if (_updating || _edit == null || editable == null) return;
        var text = editable.ToString() ?? "";
        var composing = editable is ISpannable span
            ? BaseInputConnection.GetComposingSpanStart(span)
            : -1;

        // 仍在拼音组字：只更新本地预览，不写入目标 App
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
        else
        {
            // 输入法整段替换（选词后常见）：按新稳定文本与已发送的差异提交
            if (!string.IsNullOrEmpty(stable) && stable != _sent)
            {
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
        }

        _edit.Alpha = text.Length > 0 ? 1f : 0.92f;
        if (composing < 0 && text.Length > 0)
            _ui.PostDelayed(ClearEditor, 80);
    }

    private void ClearEditor()
    {
        if (_edit == null) return;
        // 若又进入组字则不要清空
        if (_edit.EditableText is ISpannable span
            && BaseInputConnection.GetComposingSpanStart(span) >= 0)
            return;
        _updating = true;
        _sent = "";
        _edit.Text = "";
        _edit.Alpha = 0.92f;
        _updating = false;
        // 清空后保持输入法，便于继续输入
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
