#if ANDROID
using Android.Text;
using Android.Views.InputMethods;
using Android.Widget;
using FlyScreen.AppMaui.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// 绕开 Entry 默认 Keyboard/Prediction 映射，固定 ClassText 以支持中文候选栏。
/// </summary>
public sealed class ComposeImeViewHandler : EntryHandler
{
    public ComposeImeViewHandler()
        : base(CreateMapper(), CommandMapper)
    {
    }

    private static IPropertyMapper<IEntry, IEntryHandler> CreateMapper()
    {
        var mapper = new PropertyMapper<IEntry, IEntryHandler>(Mapper);
        mapper.ModifyMapping(nameof(Entry.Keyboard), (handler, view, _) => ApplyImeFriendlyInput(handler));
        mapper.ModifyMapping(nameof(Entry.IsTextPredictionEnabled), (handler, view, _) => ApplyImeFriendlyInput(handler));
        mapper.ModifyMapping(nameof(Entry.IsSpellCheckEnabled), (handler, view, _) => ApplyImeFriendlyInput(handler));
        return mapper;
    }

    protected override MauiAppCompatEditText CreatePlatformView()
    {
        var edit = new MauiAppCompatEditText(Context ?? throw new InvalidOperationException("no Context"))
        {
            ShowSoftInputOnFocus = true,
            InputType = InputTypes.ClassText,
            ImeOptions = ImeAction.Done
        };
        edit.SetSingleLine(true);
        edit.SetMaxLines(1);
        edit.SetHorizontallyScrolling(true);
        edit.SetBackgroundColor(global::Android.Graphics.Color.Transparent);
        return edit;
    }

    public override void SetVirtualView(IView view)
    {
        base.SetVirtualView(view);
        ApplyImeFriendlyInput(this);
    }

    private static void ApplyImeFriendlyInput(IEntryHandler handler)
    {
        if (handler.PlatformView is not EditText edit) return;
        edit.ShowSoftInputOnFocus = true;
        edit.InputType = InputTypes.ClassText;
        edit.ImeOptions = ImeAction.Done;
        edit.SetSingleLine(true);
        try { edit.PrivateImeOptions = null; } catch { /* ignore */ }
    }

    public static bool IsComposing(EditText? edit)
    {
        try
        {
            if (edit?.EditableText is not ISpannable span) return false;
            return BaseInputConnection.GetComposingSpanStart(span) >= 0;
        }
        catch
        {
            return false;
        }
    }
}
#endif
