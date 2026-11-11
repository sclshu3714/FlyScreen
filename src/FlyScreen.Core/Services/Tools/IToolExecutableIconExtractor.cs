namespace FlyScreen.Services.Tools;

/// <summary>从本地可执行文件提取卡片 PNG 图标（各端可选挂接）。</summary>
public interface IToolExecutableIconExtractor
{
    /// <summary>成功返回 PNG 字节；失败返回 null。</summary>
    byte[]? TryExtractPng(string executablePath);
}
