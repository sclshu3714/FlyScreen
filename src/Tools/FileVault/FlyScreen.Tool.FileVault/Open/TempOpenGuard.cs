using System.Diagnostics;
using FlyScreen.Tool.FileVault.Store;

namespace FlyScreen.Tool.FileVault.Open;

/// <summary>将单个文件解密到临时目录并用系统默认程序打开；跟踪后尽力擦除。</summary>
internal sealed class TempOpenGuard : IDisposable
{
    private readonly List<string> _temps = [];
    private readonly object _gate = new();

    public string OpenFile(VaultRepository repo, string entryId)
    {
        var entry = repo.Get(entryId) ?? throw new InvalidOperationException("条目不存在");
        if (entry.IsDir)
            throw new InvalidOperationException("不能打开目录");

        var dir = Path.Combine(Path.GetTempPath(), "FlyScreenVaultOpen", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, entry.Name);
        repo.ExportFile(entryId, dest, progress: null, CancellationToken.None);
        lock (_gate) _temps.Add(dir);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = dest,
                UseShellExecute = true
            });
        }
        catch
        {
            // 仍返回路径，调用方可提示用户手动打开
        }

        return dest;
    }

    public void CleanupAll()
    {
        List<string> copy;
        lock (_gate)
        {
            copy = _temps.ToList();
            _temps.Clear();
        }
        foreach (var dir in copy)
            SecureDelete.DirectoryTree(dir);
    }

    public void Dispose() => CleanupAll();
}
