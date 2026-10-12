using System.IO;
using System.Text;
using FlyScreen.Helpers;
using WinForms = System.Windows.Forms;

namespace FlyScreen.Services.Clipboard;

/// <summary>
/// OLE 剪贴板：先占位启用“粘贴”；拉取在后台进行，GetData 时用消息泵等待，避免 UI/Explorer 死锁。
/// </summary>
internal sealed class PendingFileDropDataObject : WinForms.DataObject
{
    public const string PreferredDropEffectFormat = "Preferred DropEffect";

    private readonly Func<Task<string[]?>> _materializeAsync;
    private readonly string[] _placeholderPaths;
    private readonly object _gate = new();
    private Task<string[]?>? _pullTask;
    private bool _merged;

    public PendingFileDropDataObject(
        IReadOnlyList<string> placeholderPaths,
        Func<Task<string[]?>> materializeAsync)
    {
        _placeholderPaths = placeholderPaths.ToArray();
        _materializeAsync = materializeAsync;

        SetData(WinForms.DataFormats.FileDrop, autoConvert: false, _placeholderPaths);
        SetData(PreferredDropEffectFormat, autoConvert: false,
            BitConverter.GetBytes((int)WinForms.DragDropEffects.Copy));
    }

    public override bool GetDataPresent(string format, bool autoConvert)
    {
        // 只声明“有文件可粘贴”，绝不在此开始网络拉取
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

    public override object? GetData(string format, bool autoConvert)
    {
        if (IsDropEffectFormat(format))
            return BitConverter.GetBytes((int)WinForms.DragDropEffects.Copy);

        if (IsFileDropFormat(format))
        {
            EnsureMaterialized();
            return _placeholderPaths;
        }

        return base.GetData(format, autoConvert);
    }

    public override object? GetData(Type format)
    {
        if (format == typeof(string[]))
        {
            EnsureMaterialized();
            return _placeholderPaths;
        }

        return base.GetData(format);
    }

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

    private void EnsureMaterialized()
    {
        EnsurePullStarted();
        var task = _pullTask!;

        // 关键须在持锁时等待；在 UI 线程上泵送消息，让接收回调能更新列表
        if (!task.IsCompleted)
            UiThread.WaitWithPumping(task, TimeSpan.FromMinutes(30));

        lock (_gate)
        {
            if (_merged) return;
            _merged = true;
            try
            {
                string[]? files = null;
                if (task.IsCompletedSuccessfully)
                    files = task.Result;

                if (files is { Length: > 0 })
                    MergeIntoPlaceholders(files, _placeholderPaths);
            }
            catch
            {
                // 保留占位
            }
        }
    }

    private static void MergeIntoPlaceholders(string[] materialized, string[] placeholders)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in materialized)
        {
            if (File.Exists(path))
                byName[Path.GetFileName(path)] = path;
        }

        for (var i = 0; i < placeholders.Length; i++)
        {
            var dest = placeholders[i];
            var name = Path.GetFileName(dest);
            if (!byName.TryGetValue(name, out var src))
            {
                if (i < materialized.Length && File.Exists(materialized[i]))
                    src = materialized[i];
                else
                    continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                    continue;

                if (File.Exists(dest))
                    File.Delete(dest);

                if (!Native.NativeMethods.CreateHardLink(dest, src, IntPtr.Zero))
                    File.Copy(src, dest, overwrite: true);
            }
            catch
            {
                try { File.Copy(src, dest, overwrite: true); } catch { /* ignore */ }
            }
        }
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
        foreach (var p in _placeholderPaths)
            sb.Append(Path.GetFileName(p)).Append(';');
        return sb.ToString();
    }
}
