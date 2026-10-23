using System.IO;
using System.Text;
using FlyScreen.Helpers;
using WinForms = System.Windows.Forms;

namespace FlyScreen.Services.Clipboard;

/// <summary>
/// OLE 懒粘贴：仅声明「有文件」，绝不在构造时 SetData 空路径。
/// 资源管理器真正 GetData（粘贴）时才拉取，并返回已写满内容的路径。
/// </summary>
internal sealed class PendingFileDropDataObject : WinForms.DataObject
{
    public const string PreferredDropEffectFormat = "Preferred DropEffect";

    private readonly Func<Task<string[]?>> _materializeAsync;
    private readonly string[] _announceNames;
    private readonly object _gate = new();
    private Task<string[]?>? _pullTask;
    private string[]? _livePaths;
    private bool _resolved;

    public PendingFileDropDataObject(
        IReadOnlyList<string> announcePlaceholderPaths,
        Func<Task<string[]?>> materializeAsync)
    {
        // 仅名称提示；禁止 SetData(FileDrop, 空占位)——否则资源管理器会直接复制 0KB
        _announceNames = announcePlaceholderPaths
            .Select(p => Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            .Where(n => !string.IsNullOrEmpty(n))
            .ToArray()!;
        _materializeAsync = materializeAsync;

        SetData(PreferredDropEffectFormat, autoConvert: false,
            BitConverter.GetBytes((int)WinForms.DragDropEffects.Copy));
    }

    public override bool GetDataPresent(string format, bool autoConvert)
    {
        if (IsFileDropFormat(format) || IsDropEffectFormat(format))
            return true;
        return base.GetDataPresent(format, autoConvert);
    }

    public override bool GetDataPresent(Type format)
    {
        if (format == typeof(string[]) || format == typeof(string))
            return true;
        return base.GetDataPresent(format);
    }

    public override string[] GetFormats(bool autoConvert)
    {
        return
        [
            WinForms.DataFormats.FileDrop,
            PreferredDropEffectFormat
        ];
    }

    [Obsolete("DataObject.GetData is obsolete; kept for OLE FileDrop compatibility.")]
#pragma warning disable WFDEV005
    public override object? GetData(string format, bool autoConvert)
    {
        if (IsDropEffectFormat(format))
            return BitConverter.GetBytes((int)WinForms.DragDropEffects.Copy);

        if (IsFileDropFormat(format))
            return ResolveLivePaths();

        return base.GetData(format, autoConvert);
    }

    [Obsolete("DataObject.GetData is obsolete; kept for OLE FileDrop compatibility.")]
    public override object? GetData(Type format)
    {
        if (format == typeof(string[]))
            return ResolveLivePaths();

        return base.GetData(format);
    }
#pragma warning restore WFDEV005

    private void EnsurePullStarted()
    {
        lock (_gate)
        {
            _pullTask ??= Task.Run(async () =>
            {
                try
                {
                    return await _materializeAsync().ConfigureAwait(false);
                }
                catch
                {
                    return null;
                }
            });
        }
    }

    private string[] ResolveLivePaths()
    {
        EnsurePullStarted();
        var task = _pullTask!;

        if (!task.IsCompleted)
            UiThread.WaitWithPumping(task, TimeSpan.FromMinutes(30));

        lock (_gate)
        {
            if (!_resolved)
            {
                _resolved = true;
                try
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        var files = task.Result;
                        if (files is { Length: > 0 } && PathsReady(files))
                            _livePaths = files;
                    }
                }
                catch
                {
                    // leave null
                }
            }

            if (_livePaths is { Length: > 0 })
            {
                // 物化后写入 DataObject，供后续查询；此前绝不 SetData 空路径
                try
                {
                    SetData(WinForms.DataFormats.FileDrop, autoConvert: false, _livePaths);
                }
                catch
                {
                    // ignore
                }

                return _livePaths;
            }

            return [];
        }
    }

    private static bool PathsReady(IReadOnlyList<string> paths)
    {
        foreach (var p in paths)
        {
            try
            {
                if (File.Exists(p) || Directory.Exists(p))
                    return true;
            }
            catch
            {
                // continue
            }
        }

        return false;
    }

    private static bool IsFileDropFormat(string? format)
    {
        if (string.IsNullOrEmpty(format)) return false;
        return format.Equals(WinForms.DataFormats.FileDrop, StringComparison.OrdinalIgnoreCase)
               || format.Equals("FileDrop", StringComparison.OrdinalIgnoreCase)
               || format.Equals("FileName", StringComparison.OrdinalIgnoreCase)
               || format.Equals("FileNameW", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDropEffectFormat(string? format)
        => !string.IsNullOrEmpty(format)
           && format.Equals(PreferredDropEffectFormat, StringComparison.OrdinalIgnoreCase);

    public override string ToString()
    {
        var sb = new StringBuilder("PendingFileDrop:");
        foreach (var n in _announceNames)
            sb.Append(n).Append(';');
        return sb.ToString();
    }
}
