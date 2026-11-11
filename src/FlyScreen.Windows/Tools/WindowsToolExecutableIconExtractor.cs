using System.Drawing;
using System.Drawing.Imaging;
using FlyScreen.Services;
using FlyScreen.Services.Tools;

namespace FlyScreen.Tools;

/// <summary>Windows：从 exe/关联文件提取 PNG 图标供工具卡片使用。</summary>
public sealed class WindowsToolExecutableIconExtractor : IToolExecutableIconExtractor
{
    public byte[]? TryExtractPng(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            return null;

        try
        {
            using var icon = Icon.ExtractAssociatedIcon(executablePath);
            if (icon == null) return null;
            using var bmp = icon.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            AppLog.Warn("提取程序图标失败: " + ex.Message);
            return null;
        }
    }
}
