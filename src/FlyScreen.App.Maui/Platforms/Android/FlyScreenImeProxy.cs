#if ANDROID
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// 把电脑按键交给手机当前输入法。拼音还在组字时不写入目标；输入法确认后的汉字再送进原来的输入框。
/// 窗口不接收触摸，手指操作不受影响。
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
                (int)Math.Ceiling(36 * density),
                type,
                WindowManagerFlags.NotTouchable
                | WindowManagerFlags.NotTouchModal
                | WindowManagerFlags.LayoutInScreen
                | WindowManagerFlags.AltFocusableIm,
                global::Android.Graphics.Format.Translucent)
            {
                Gravity = GravityFlags.Bottom | GravityFlags.Left,
                SoftInputMode = SoftInput.StateAlwaysHidden,
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
        var ev = new KeyEvent(now, now, action, (Keycode)keyCode, 0, meta);
        return _edit.DispatchKeyEvent(ev);
    }

    public void Detach()
    {
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
            ShowSoftInputOnFocus = false,
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
        imm?.RestartInput(_edit);
    }

    private void OnText(IEditable? editable)
    {
        if (_updating || _edit == null || editable == null) return;
        var text = editable.ToString() ?? "";
        var composing = editable is ISpannable span
            ? BaseInputConnection.GetComposingSpanStart(span)
            : -1;
        var stableEnd = composing >= 0 ? Math.Clamp(composing, 0, text.Length) : text.Length;
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
            _sent = stable;
        }

        _edit.Alpha = text.Length > 0 ? 1f : 0f;
        if (composing < 0 && text.Length > 0)
            _ui.Post(ClearEditor);
    }

    private void ClearEditor()
    {
        if (_edit == null) return;
        _updating = true;
        _sent = "";
        _edit.Text = "";
        _edit.Alpha = 0f;
        _updating = false;
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
