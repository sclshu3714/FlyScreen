namespace FlyScreen.ToolSdk;

/// <summary>
/// 工具图标贡献：资源在工具程序集内，宿主按 <see cref="IconKey"/> 解析，不按工具写死分支。
/// </summary>
public interface IToolIconContribution
{
    /// <summary>与 manifest <c>icon</c> / 卡片 <c>IconKey</c> 一致。</summary>
    string IconKey { get; }

    /// <summary>打开嵌入的位图流（jpg/png）；无位图时返回 null。</summary>
    Stream? OpenBitmap();
}
