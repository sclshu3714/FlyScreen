using FlyScreen.Helpers.Localization;

namespace FlyScreen.Helpers;

/// <summary>关于页结构化内容：纯文本 + 可跳转链接（仅 https / mailto）。</summary>
public static class AboutDocument
{
    public const string RepoUrl = "https://github.com/sclshu3714/FlyScreen";
    public const string ReleasesUrl = "https://github.com/sclshu3714/FlyScreen/releases";
    public const string IssuesUrl = "https://github.com/sclshu3714/FlyScreen/issues";
    /// <summary>本仓库 Apache-2.0 许可页（GitHub License 选项卡）。</summary>
    public const string LicenseUrl = "https://github.com/sclshu3714/FlyScreen?tab=Apache-2.0-1-ov-file";
    public const string ThirdPartyUrl = "https://github.com/sclshu3714/FlyScreen/blob/master/docs/THIRD_PARTY.md";
    public const string NoticeUrl = "https://github.com/sclshu3714/FlyScreen/blob/master/NOTICE.txt";
    public const string EmailMailto = "mailto:sclshu3714@163.com";
    public const string MlKitTermsUrl = "https://developers.google.com/ml-kit/terms";

    public readonly record struct Span(string Text, string? Url = null);

    public sealed class Paragraph
    {
        public List<Span> Spans { get; } = new();
        public bool IsHeading { get; init; }

        public static Paragraph Heading(string text) => new() { IsHeading = true, Spans = { new Span(text) } };

        public static Paragraph Text(string text) => new() { Spans = { new Span(text) } };

        public static Paragraph Blank() => new();
    }

    public static IReadOnlyList<Paragraph> Build(string version)
    {
        var list = new List<Paragraph>();

        list.Add(Paragraph.Text(Loc.T("About.AppLine", version)));
        list.Add(Paragraph.Blank());

        list.Add(Paragraph.Heading(Loc.T("About.Sec.Intro")));
        list.Add(Paragraph.Text(Loc.T("About.IntroBody")));
        list.Add(Paragraph.Blank());

        list.Add(Paragraph.Heading(Loc.T("About.Sec.License")));
        list.Add(Paragraph.Text(Loc.T("About.Copyright")));
        list.Add(P(
            Loc.T("About.LicenseName"),
            "Apache License 2.0", LicenseUrl,
            Loc.T("About.LicenseFull")));
        list.Add(Paragraph.Text(Loc.T("About.LicenseRedistrib")));
        list.Add(Paragraph.Text(Loc.T("About.Disclaimer")));
        list.Add(Paragraph.Blank());

        list.Add(Paragraph.Heading(Loc.T("About.Sec.Author")));
        list.Add(Paragraph.Text(Loc.T("About.AuthorLine")));
        list.Add(P(Loc.T("About.EmailLabel"), "sclshu3714@163.com", EmailMailto));
        list.Add(P(Loc.T("About.FeedbackLabel"), Loc.T("About.FeedbackIssues"), IssuesUrl));
        list.Add(Paragraph.Blank());

        list.Add(Paragraph.Heading(Loc.T("About.Sec.Links")));
        list.Add(P(Loc.T("About.RepoLabel"), RepoUrl, RepoUrl));
        list.Add(P(Loc.T("About.ReleaseLabel"), ReleasesUrl, ReleasesUrl));
        list.Add(P(Loc.T("About.ThirdPartyLabel"), Loc.T("About.ThirdPartyLinkText"), ThirdPartyUrl));
        list.Add(P(Loc.T("About.NoticeLabel"), "NOTICE.txt", NoticeUrl));
        list.Add(Paragraph.Blank());

        list.Add(Paragraph.Heading(Loc.T("About.Sec.ThirdParty")));
        list.Add(Paragraph.Text(Loc.T("About.OssDepsIntro")));
        // 许可链接指向各开源仓库内的许可说明（GitHub License 页 / 仓库 LICENSE），不链到协议官网
        AddOss(list,
            ".NET", "https://github.com/dotnet/runtime",
            "MIT", "https://github.com/dotnet/runtime?tab=MIT-1-ov-file");
        AddOss(list,
            ".NET MAUI", "https://github.com/dotnet/maui",
            "MIT", "https://github.com/dotnet/maui?tab=MIT-1-ov-file");
        AddOss(list,
            "Avalonia", "https://github.com/AvaloniaUI/Avalonia",
            "MIT", "https://github.com/AvaloniaUI/Avalonia?tab=MIT-1-ov-file");
        AddOss(list,
            "AvalonEdit", "https://github.com/icsharpcode/AvalonEdit",
            "MIT", "https://github.com/icsharpcode/AvalonEdit?tab=MIT-1-ov-file");
        AddOss(list,
            "AvaloniaEdit", "https://github.com/AvaloniaUI/AvaloniaEdit",
            "MIT", "https://github.com/AvaloniaUI/AvaloniaEdit?tab=MIT-1-ov-file");
        AddOss(list,
            "Monaco Editor", "https://github.com/microsoft/monaco-editor",
            "MIT", "https://github.com/microsoft/monaco-editor?tab=MIT-1-ov-file");
        AddOss(list,
            "Jint", "https://github.com/sebastienros/jint",
            "BSD-3-Clause", "https://github.com/sebastienros/jint?tab=BSD-3-Clause-1-ov-file");
        AddOss(list,
            "Roslyn", "https://github.com/dotnet/roslyn",
            "MIT", "https://github.com/dotnet/roslyn?tab=MIT-1-ov-file");
        AddOss(list,
            "AndroidX", "https://github.com/androidx/androidx",
            "Apache-2.0", "https://github.com/androidx/androidx?tab=Apache-2.0-1-ov-file");
        AddOss(list,
            "Virtual Display Driver", "https://github.com/VirtualDrivers/Virtual-Display-Driver",
            "MIT", "https://github.com/VirtualDrivers/Virtual-Display-Driver?tab=MIT-1-ov-file");
        AddOss(list,
            "Noto Sans SC", "https://github.com/notofonts/noto-cjk",
            "SIL OFL", "https://github.com/notofonts/noto-cjk?tab=OFL-1.1-1-ov-file");
        list.Add(Paragraph.Blank());

        list.Add(Paragraph.Text(Loc.T("About.PropDepsIntro")));
        list.Add(P(
            "· Google ML Kit Text Recognition — ",
            Loc.T("About.MlKitLinkText"), MlKitTermsUrl));
        list.Add(Paragraph.Text(Loc.T("About.MlKitNote")));
        list.Add(Paragraph.Blank());

        list.Add(Paragraph.Heading(Loc.T("About.Sec.Status")));
        list.Add(Paragraph.Text(Loc.T("About.StatusBody")));
        list.Add(Paragraph.Blank());

        list.Add(Paragraph.Heading(Loc.T("About.Sec.Runtime")));
        list.Add(Paragraph.Text(Loc.T("About.RuntimeBody")));

        return list;
    }

    private static Paragraph P(string prefix, string linkText, string url, string? suffix = null)
    {
        var p = new Paragraph();
        if (!string.IsNullOrEmpty(prefix))
            p.Spans.Add(new Span(prefix));
        p.Spans.Add(new Span(linkText, url));
        if (!string.IsNullOrEmpty(suffix))
            p.Spans.Add(new Span(suffix));
        return p;
    }

    private static void AddOss(List<Paragraph> list, string name, string projectUrl, string licenseName, string licenseUrl)
    {
        var p = new Paragraph();
        p.Spans.Add(new Span("· "));
        p.Spans.Add(new Span(name, projectUrl));
        p.Spans.Add(new Span(" — "));
        p.Spans.Add(new Span(licenseName, licenseUrl));
        list.Add(p);
    }
}
