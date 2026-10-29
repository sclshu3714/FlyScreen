namespace FlyScreen.Models;

/// <summary>粘贴就绪后的单个文件/目录项（供虚拟剪贴板 FileGroupDescriptor 使用）。</summary>
public readonly struct ClipboardPasteEntry
{
    public ClipboardPasteEntry(string relativePath, string fullPath, bool isDirectory, long size)
    {
        RelativePath = relativePath;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Size = size;
    }

    /// <summary>相对路径，使用反斜杠，如 folder\a.txt</summary>
    public string RelativePath { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public long Size { get; }
}
