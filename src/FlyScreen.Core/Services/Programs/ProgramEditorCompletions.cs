namespace FlyScreen.Services.Programs;

/// <summary>各语言关键字 / 常用 API，供 AvalonEdit / AvaloniaEdit / Monaco 智能提示。</summary>
public static class ProgramEditorCompletions
{
    public static IReadOnlyList<string> Keywords(ProgramLanguage lang) => lang switch
    {
        ProgramLanguage.Python => Python,
        ProgramLanguage.CSharp => CSharp,
        ProgramLanguage.JavaScript => JavaScript,
        ProgramLanguage.PowerShell => PowerShell,
        ProgramLanguage.Bash => Bash,
        ProgramLanguage.Batch => Batch,
        _ => Array.Empty<string>()
    };

    public static IReadOnlyList<string> CollectSuggestions(ProgramLanguage lang, string? documentText, string prefix)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in Keywords(lang))
        {
            if (MatchesPrefix(k, prefix))
                set.Add(k);
        }

        if (!string.IsNullOrEmpty(documentText))
        {
            foreach (var w in ExtractIdentifiers(documentText))
            {
                if (MatchesPrefix(w, prefix) && w.Length >= 2)
                    set.Add(w);
            }
        }

        return set.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).Take(80).ToList();
    }

    private static bool MatchesPrefix(string word, string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return true;
        return word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ExtractIdentifiers(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : '\0';
            var ok = char.IsLetterOrDigit(c) || c == '_';
            if (ok)
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                var len = i - start;
                if (len >= 2 && (char.IsLetter(text[start]) || text[start] == '_'))
                    yield return text.Substring(start, len);
                start = -1;
            }
        }
    }

    private static readonly string[] Python =
    [
        "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del",
        "elif", "else", "except", "False", "finally", "for", "from", "global", "if", "import",
        "in", "is", "lambda", "None", "nonlocal", "not", "or", "pass", "raise", "return",
        "True", "try", "while", "with", "yield",
        "print", "len", "range", "str", "int", "float", "list", "dict", "set", "tuple",
        "open", "input", "enumerate", "zip", "map", "filter", "sorted", "sum", "min", "max",
        "abs", "isinstance", "hasattr", "getattr", "setattr", "Exception", "ValueError",
        "TypeError", "KeyError", "IndexError", "self", "__init__", "__name__", "__main__"
    ];

    private static readonly string[] CSharp =
    [
        "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch",
        "char", "checked", "class", "const", "continue", "decimal", "default", "delegate",
        "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator",
        "out", "override", "params", "private", "protected", "public", "readonly", "ref",
        "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
        "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
        "unchecked", "unsafe", "ushort", "using", "var", "virtual", "void", "volatile", "while",
        "Console", "WriteLine", "Write", "ReadLine", "Task", "Delay", "List", "Dictionary",
        "StringBuilder", "Exception", "ArgumentException", "ArgumentNullException", "nameof",
        "async", "await", "get", "set", "value", "record", "init", "required",
        "Fly", "Ready", "App", "HasText", "HasId", "HasDesc", "Texts", "TextBounds", "Ocr", "DrawRect",
        "TapText", "LongPressText", "TapId", "TapDesc", "Tap", "TapPx", "DoubleTap", "LongPress",
        "Swipe", "SwipeUp", "SwipeDown", "SwipeLeft", "SwipeRight", "ScrollForward", "ScrollBackward",
        "WaitText", "WaitGone", "Input", "ClearInput", "Enter",
        "Back", "Home", "Recents", "Notifications", "QuickSettings", "LockScreen", "TakeScreenshot",
        "OpenSettings", "ScreenWidth", "ScreenHeight", "ct"
    ];

    private static readonly string[] JavaScript =
    [
        "await", "break", "case", "catch", "class", "const", "continue", "debugger", "default",
        "delete", "do", "else", "export", "extends", "false", "finally", "for", "function",
        "if", "import", "in", "instanceof", "let", "new", "null", "return", "static", "super",
        "switch", "this", "throw", "true", "try", "typeof", "var", "void", "while", "with",
        "yield", "async", "of", "from", "as",
        "console", "log", "error", "warn", "info", "JSON", "parse", "stringify", "Promise",
        "then", "catch", "finally", "setTimeout", "setInterval", "clearTimeout", "Array",
        "Object", "String", "Number", "Boolean", "Map", "Set", "Math", "Date", "Error",
        "require", "module", "exports", "process", "Buffer", "undefined", "NaN", "Infinity",
        "Fly", "ready", "app", "hasText", "hasId", "hasDesc", "texts", "textBounds", "ocr", "drawRect",
        "tapText", "longPressText", "tapId", "tapDesc", "tap", "tapPx", "doubleTap", "longPress",
        "swipe", "swipeUp", "swipeDown", "swipeLeft", "swipeRight", "scrollForward", "scrollBackward",
        "waitText", "waitGone", "input", "clearInput", "enter",
        "back", "home", "recents", "notifications", "quickSettings", "lockScreen", "takeScreenshot",
        "openSettings", "sleep", "screenWidth", "screenHeight"
    ];

    private static readonly string[] PowerShell =
    [
        "Begin", "Break", "Catch", "Continue", "Data", "Do", "DynamicParam", "Else", "ElseIf",
        "End", "Exit", "Filter", "Finally", "For", "ForEach", "From", "Function", "If", "In",
        "Param", "Process", "Return", "Switch", "Throw", "Trap", "Try", "Until", "Using",
        "Var", "While", "Write-Host", "Write-Output", "Write-Error", "Get-Content", "Set-Content",
        "Get-ChildItem", "Get-Item", "New-Item", "Remove-Item", "Copy-Item", "Move-Item",
        "Select-Object", "Where-Object", "ForEach-Object", "Import-Module", "Export-ModuleMember"
    ];

    private static readonly string[] Bash =
    [
        "if", "then", "else", "elif", "fi", "for", "while", "until", "do", "done", "case",
        "esac", "function", "select", "time", "coproc", "in", "[[", "]]",
        "echo", "printf", "read", "cd", "pwd", "ls", "cp", "mv", "rm", "mkdir", "rmdir",
        "cat", "grep", "sed", "awk", "find", "chmod", "chown", "export", "source", "alias",
        "exit", "return", "local", "declare", "typeset", "readonly", "unset", "shift", "test"
    ];

    private static readonly string[] Batch =
    [
        "echo", "set", "setlocal", "endlocal", "if", "else", "for", "goto", "call", "exit",
        "pause", "rem", "cd", "dir", "copy", "move", "del", "mkdir", "rmdir", "type",
        "find", "findstr", "errorlevel", "exist", "defined", "not", "equ", "neq", "lss",
        "leq", "gtr", "geq", "pushd", "popd", "shift", "start", "title", "color", "cls"
    ];
}
