using Avalonia.Input;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using FlyScreen.Services.Programs;

namespace FlyScreen.DesktopUi.Editor;

/// <summary>AvaloniaEdit 关键字 + 文档标识符补全。</summary>
internal sealed class EditorCompletion
{
    private readonly TextEditor _editor;
    private readonly Func<ProgramLanguage> _language;
    private CompletionWindow? _window;

    public EditorCompletion(TextEditor editor, Func<ProgramLanguage> language)
    {
        _editor = editor;
        _language = language;
        editor.TextArea.TextEntered += OnTextEntered;
        editor.TextArea.TextEntering += OnTextEntering;
        editor.TextArea.KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            Show(force: true);
        }
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        var c = e.Text[0];
        if (char.IsLetter(c) || c is '_' or '.')
            Show(force: false);
    }

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || _window == null) return;
        if (!char.IsLetterOrDigit(e.Text[0]) && e.Text[0] != '_')
            _window.CompletionList.RequestInsertion(e);
    }

    private void Show(bool force)
    {
        var area = _editor.TextArea;
        var doc = _editor.Document;
        if (doc == null) return;

        GetWordBeforeCaret(doc, area.Caret.Offset, out var start, out var prefix);
        if (!force && prefix.Length < 1) return;

        var items = ProgramEditorCompletions.CollectSuggestions(_language(), doc.Text, prefix);
        if (items.Count == 0) return;

        _window?.Close();
        _window = new CompletionWindow(area)
        {
            CloseWhenCaretAtBeginning = true
        };
        _window.StartOffset = start;
        _window.EndOffset = area.Caret.Offset;
        foreach (var s in items)
            _window.CompletionList.CompletionData.Add(new KeywordCompletionData(s));
        _window.Closed += (_, _) => _window = null;
        _window.Show();
    }

    private static void GetWordBeforeCaret(TextDocument doc, int caret, out int start, out string prefix)
    {
        start = caret;
        while (start > 0)
        {
            var c = doc.GetCharAt(start - 1);
            if (!char.IsLetterOrDigit(c) && c != '_') break;
            start--;
        }
        prefix = start < caret ? doc.GetText(start, caret - start) : "";
    }

    private sealed class KeywordCompletionData : ICompletionData
    {
        public KeywordCompletionData(string text) => Text = text;
        public Avalonia.Media.IImage? Image => null;
        public string Text { get; }
        public object Content => Text;
        public object Description => Text;
        public double Priority => 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
            => textArea.Document.Replace(completionSegment, Text);
    }
}
