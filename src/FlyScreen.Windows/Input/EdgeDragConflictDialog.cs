using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FlyScreen.Services.Clipboard;
using FlyScreen.Services.Network;

namespace FlyScreen.Input;

/// <summary>
/// 仿资源管理器「替换或跳过文件」：命令链接按钮 + 比较信息 + 更多详细信息。
/// Win 桌面与 Maui-Win 共用。
/// </summary>
public static class EdgeDragConflictDialog
{
    private const int ReplaceId = 1001;
    private const int SkipId = 1002;
    private const int CompareId = 1003;
    private const int IdCancel = 2;
    private const int TdfAllowCancellation = 0x0008;
    private const int TdfUseCommandLinks = 0x0010;
    private const int TdfExpandFooterArea = 0x0002;
    private const int TdfPositionRelativeToWindow = 0x1000;
    private const int TdfSizeToContent = 0x01000000;
    private const int TdnButtonClicked = 2;
    private const int SOk = 0;
    private const int SFalse = 1;

    private static string _compareText = "";

    public static EdgeDragConflictAction Ask(EdgeDragConflictRequest request)
    {
        if (request.Conflicts.Count == 0) return EdgeDragConflictAction.Proceed;
        var dest = FolderLabel(request.Directory);
        var source = string.IsNullOrWhiteSpace(request.SourceFolder) ? "对端" : request.SourceFolder.Trim();
        var one = request.Conflicts.Count == 1;
        var title = "替换或跳过文件";
        var content = $"正在将 {Math.Max(request.TotalCount, request.Conflicts.Count)} 个项目从 {source} 复制到 {dest}";
        var instruction = one
            ? $"目标已包含一个名为“{request.Conflicts[0]}”的文件"
            : $"目标已包含 {request.Conflicts.Count} 个同名项目";

        _compareText = BuildCompareText(request);
        var details = BuildDetailsText(request);
        var skipLabel = one ? "跳过该文件" : "跳过这些文件";
        var choice = AskTask(
            title,
            instruction,
            content,
            "替换目标中的文件",
            skipLabel,
            "比较两个文件的信息",
            details);
        if (choice != null) return choice.Value;

        // TaskDialog 在部分环境（无 comctl v6 / WinUI·Maui 主线程）会直接失败或返回怪按钮，绝不能静默当取消
        return AskMessageBox(title, instruction, content, skipLabel);
    }

    /// <summary>
    /// Maui-Win / 无 TaskDialog 环境：只用 MessageBox，避免「没弹窗却 Cancel」导致 Win→Maui 拖放失败。
    /// </summary>
    public static EdgeDragConflictAction AskMessageBox(EdgeDragConflictRequest request)
    {
        if (request.Conflicts.Count == 0) return EdgeDragConflictAction.Proceed;
        var dest = FolderLabel(request.Directory);
        var source = string.IsNullOrWhiteSpace(request.SourceFolder) ? "对端" : request.SourceFolder.Trim();
        var one = request.Conflicts.Count == 1;
        var title = "替换或跳过文件";
        var content = $"正在将 {Math.Max(request.TotalCount, request.Conflicts.Count)} 个项目从 {source} 复制到 {dest}";
        var instruction = one
            ? $"目标已包含一个名为“{request.Conflicts[0]}”的文件"
            : $"目标已包含 {request.Conflicts.Count} 个同名项目";
        var skipLabel = one ? "跳过该文件" : "跳过这些文件";
        return AskMessageBox(title, instruction, content, skipLabel);
    }

    private static EdgeDragConflictAction AskMessageBox(
        string title, string instruction, string content, string skipLabel)
    {
        var text = instruction + "\n\n" + content + "\n\n是 = 替换目标中的文件\n否 = " + skipLabel + "\n取消 = 取消本次拖放";
        var answer = MessageBox(GetForegroundWindow(), text, title,
            0x00000003 | 0x00000030 | 0x00010000 | 0x00040000);
        return answer switch
        {
            6 => EdgeDragConflictAction.Replace,
            7 => EdgeDragConflictAction.Skip,
            _ => EdgeDragConflictAction.Cancel
        };
    }

    /// <summary>旧签名兼容。</summary>
    public static EdgeDragConflictAction Ask(string directory, IReadOnlyList<string> conflicts, int totalCount)
        => Ask(new EdgeDragConflictRequest
        {
            Directory = directory,
            Conflicts = conflicts,
            TotalCount = totalCount
        });

    private static string FolderLabel(string directory)
    {
        try
        {
            var trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        catch
        {
            // 用原路径
        }

        return string.IsNullOrWhiteSpace(directory) ? "目标文件夹" : directory;
    }

    private static string BuildDetailsText(EdgeDragConflictRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"目标文件夹：{request.Directory}");
        if (!string.IsNullOrWhiteSpace(request.SourceFolder))
            sb.AppendLine($"源文件夹：{request.SourceFolder}");
        foreach (var name in request.Conflicts.Take(12))
        {
            var src = FindSource(request.SourceItems, name);
            var destPath = SafeCombine(request.Directory, name);
            sb.AppendLine();
            sb.AppendLine(name);
            sb.AppendLine("  正在复制：" + Describe(src, pendingPath: true));
            sb.AppendLine("  已经存在：" + DescribeDest(destPath));
        }

        if (request.Conflicts.Count > 12)
            sb.AppendLine($"…以及其他 {request.Conflicts.Count - 12} 项");
        return sb.ToString().TrimEnd();
    }

    private static string BuildCompareText(EdgeDragConflictRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("比较两个文件的信息");
        sb.AppendLine();
        foreach (var name in request.Conflicts.Take(8))
        {
            var src = FindSource(request.SourceItems, name);
            var destPath = SafeCombine(request.Directory, name);
            sb.AppendLine($"文件：{name}");
            sb.AppendLine($"  正在复制的文件\t{Describe(src, pendingPath: true)}");
            sb.AppendLine($"  已有的文件\t{DescribeDest(destPath)}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static EdgeDragItemInfo? FindSource(IReadOnlyList<EdgeDragItemInfo> items, string name)
    {
        foreach (var item in items)
        {
            if (string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
                return item;
        }

        return null;
    }

    private static string SafeCombine(string directory, string name)
    {
        try { return Path.Combine(directory, name); }
        catch { return ""; }
    }

    private static string Describe(EdgeDragItemInfo? info, bool pendingPath)
    {
        if (info == null) return "未知";
        if (info.IsDirectory) return FormatStamp(-1, info.LastWriteUtcTicks, folder: true);
        return FormatStamp(info.Size, info.LastWriteUtcTicks, folder: false);
    }

    private static string DescribeDest(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "未知";
        try
        {
            if (Directory.Exists(path))
                return FormatStamp(-1, Directory.GetLastWriteTimeUtc(path).Ticks, folder: true);
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                return FormatStamp(fi.Length, fi.LastWriteTimeUtc.Ticks, folder: false);
            }
        }
        catch
        {
            // 读不到
        }

        return "未知";
    }

    private static string FormatStamp(long size, long utcTicks, bool folder)
    {
        var time = utcTicks > 0
            ? new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy/M/d HH:mm", CultureInfo.InvariantCulture)
            : "未知时间";
        if (folder) return $"文件夹，{time}";
        if (size < 0) return time;
        return $"{FormatSize(size)}，{time}";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} 字节";
        double n = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        var u = -1;
        do
        {
            n /= 1024;
            u++;
        } while (n >= 1024 && u < units.Length - 1);

        return $"{n:0.##} {units[u]}";
    }

    private static EdgeDragConflictAction? AskTask(
        string title,
        string instruction,
        string content,
        string replaceLabel,
        string skipLabel,
        string compareLabel,
        string details)
    {
        var replace = Marshal.StringToHGlobalUni(replaceLabel);
        var skip = Marshal.StringToHGlobalUni(skipLabel);
        var compare = Marshal.StringToHGlobalUni(compareLabel);
        var titlePtr = Marshal.StringToHGlobalUni(title);
        var instructionPtr = Marshal.StringToHGlobalUni(instruction);
        var contentPtr = Marshal.StringToHGlobalUni(content);
        var expandedPtr = Marshal.StringToHGlobalUni(details);
        var expandText = Marshal.StringToHGlobalUni("更多详细信息");
        var collapseText = Marshal.StringToHGlobalUni("简略信息");
        var buttons = Marshal.AllocHGlobal(Marshal.SizeOf<TaskDialogButton>() * 3);
        var callback = new TaskDialogCallbackProc(OnCallback);
        try
        {
            WriteButton(buttons, 0, ReplaceId, replace);
            WriteButton(buttons, 1, SkipId, skip);
            WriteButton(buttons, 2, CompareId, compare);
            var config = new TaskDialogConfig
            {
                cbSize = (uint)Marshal.SizeOf<TaskDialogConfig>(),
                hwndParent = GetForegroundWindow(),
                // 不用 SIZE_TO_CONTENT：部分系统上会导致 TaskDialogIndirect 直接失败
                dwFlags = TdfAllowCancellation | TdfUseCommandLinks | TdfExpandFooterArea
                          | TdfPositionRelativeToWindow,
                dwCommonButtons = 0,
                pszWindowTitle = titlePtr,
                pszMainInstruction = instructionPtr,
                pszContent = contentPtr,
                cButtons = 3,
                pButtons = buttons,
                nDefaultButton = ReplaceId,
                pszExpandedInformation = expandedPtr,
                pszExpandedControlText = collapseText,
                pszCollapsedControlText = expandText,
                pfCallback = Marshal.GetFunctionPointerForDelegate(callback)
            };
            var hr = TaskDialogIndirect(ref config, out var button, out _, out _);
            GC.KeepAlive(callback);
            if (hr < 0) return null;
            // 未识别的按钮码（含 0）视为失败，交给 MessageBox；只有明确点取消才 Cancel
            return button switch
            {
                ReplaceId => EdgeDragConflictAction.Replace,
                SkipId => EdgeDragConflictAction.Skip,
                IdCancel => EdgeDragConflictAction.Cancel,
                _ => null
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buttons);
            Marshal.FreeHGlobal(replace);
            Marshal.FreeHGlobal(skip);
            Marshal.FreeHGlobal(compare);
            Marshal.FreeHGlobal(titlePtr);
            Marshal.FreeHGlobal(instructionPtr);
            Marshal.FreeHGlobal(contentPtr);
            Marshal.FreeHGlobal(expandedPtr);
            Marshal.FreeHGlobal(expandText);
            Marshal.FreeHGlobal(collapseText);
        }
    }

    private static int OnCallback(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr refData)
    {
        if (msg == TdnButtonClicked && wParam.ToInt32() == CompareId)
        {
            MessageBox(hwnd, _compareText, "比较两个文件的信息", 0x00000040 | 0x00010000);
            return SFalse;
        }

        return SOk;
    }

    private static void WriteButton(IntPtr array, int index, int id, IntPtr text)
    {
        var size = Marshal.SizeOf<TaskDialogButton>();
        var button = new TaskDialogButton { nButtonID = id, pszButtonText = text };
        Marshal.StructureToPtr(button, array + index * size, false);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int TaskDialogCallbackProc(
        IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr refData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TaskDialogButton
    {
        public int nButtonID;
        public IntPtr pszButtonText;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TaskDialogConfig
    {
        public uint cbSize;
        public IntPtr hwndParent;
        public IntPtr hInstance;
        public int dwFlags;
        public int dwCommonButtons;
        public IntPtr pszWindowTitle;
        public IntPtr hMainIcon;
        public IntPtr pszMainInstruction;
        public IntPtr pszContent;
        public uint cButtons;
        public IntPtr pButtons;
        public int nDefaultButton;
        public uint cRadioButtons;
        public IntPtr pRadioButtons;
        public int nDefaultRadioButton;
        public IntPtr pszVerificationText;
        public IntPtr pszExpandedInformation;
        public IntPtr pszExpandedControlText;
        public IntPtr pszCollapsedControlText;
        public IntPtr hFooterIcon;
        public IntPtr pszFooter;
        public IntPtr pfCallback;
        public IntPtr lpCallbackData;
        public uint cxWidth;
    }

    [DllImport("comctl32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int TaskDialogIndirect(
        ref TaskDialogConfig config, out int button, out int radio, out bool verification);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
