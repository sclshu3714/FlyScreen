using FlyScreen.Tool.VideoWall.Contracts;
using FlyScreen.Tool.VideoWall.Sources;
using Xunit;

namespace FlyScreen.Tool.VideoWall.Tests;

public sealed class SourceHeaderTextTests
{
    [Fact]
    public void Format_And_Parse_RoundTrip()
    {
        var src = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Referer"] = "https://example.com",
            ["User-Agent"] = "FlyScreen/1.0"
        };

        var text = SourceHeaderText.Format(src);
        var back = SourceHeaderText.Parse(text);

        Assert.Equal(2, back.Count);
        Assert.Equal("https://example.com", back["Referer"]);
        Assert.Equal("FlyScreen/1.0", back["User-Agent"]);
    }

    [Fact]
    public void Parse_Accepts_Equals_And_Ignores_Blank()
    {
        var text = """
            Referer: https://a.test
            # comment
            User-Agent=Bot

            Cookie: a=1
            """;

        var headers = SourceHeaderText.Parse(text);
        Assert.Equal(3, headers.Count);
        Assert.Equal("https://a.test", headers["Referer"]);
        Assert.Equal("Bot", headers["User-Agent"]);
        Assert.Equal("a=1", headers["Cookie"]);
    }

    [Fact]
    public void Parse_Caps_At_MaxEntries()
    {
        var lines = Enumerable.Range(1, 12).Select(i => $"H{i}: v{i}");
        var headers = SourceHeaderText.Parse(string.Join('\n', lines));
        Assert.Equal(SourceHeaderText.MaxEntries, headers.Count);
    }

    [Fact]
    public void TryBuild_Persists_Headers()
    {
        using var tmp = new TempCredentialStore();
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = "测试",
            ["address"] = "https://cdn.example.com/live.m3u8",
            ["auth"] = SourceFormSchema.AuthNone,
            ["headers"] = "Referer: https://example.com\nUser-Agent: UA",
            ["decode"] = "auto"
        };

        var descriptor = SourceDescriptorBuilder.TryBuild(
            VideoSourceKind.NetworkUrl, values, tmp.Store, out var err);

        Assert.Null(err);
        Assert.NotNull(descriptor);
        Assert.Equal(2, descriptor!.Endpoint.Headers.Count);
        Assert.Equal("https://example.com", descriptor.Endpoint.Headers["Referer"]);
        Assert.Equal("UA", descriptor.Endpoint.Headers["User-Agent"]);
    }

    [Fact]
    public void TryBuild_Without_Headers_Key_Leaves_Empty()
    {
        using var tmp = new TempCredentialStore();
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = "cam",
            ["address"] = "rtsp://192.168.1.10/stream",
            ["auth"] = SourceFormSchema.AuthNone,
            ["decode"] = "auto"
        };

        var descriptor = SourceDescriptorBuilder.TryBuild(
            VideoSourceKind.Camera, values, tmp.Store, out var err);

        Assert.Null(err);
        Assert.NotNull(descriptor);
        Assert.Empty(descriptor!.Endpoint.Headers);
    }

    private sealed class TempCredentialStore : IDisposable
    {
        private readonly string _path;
        public CredentialStore Store { get; }

        public TempCredentialStore()
        {
            _path = Path.Combine(Path.GetTempPath(), "vw-cred-" + Guid.NewGuid().ToString("N") + ".bin");
            Store = new CredentialStore(_path);
        }

        public void Dispose()
        {
            try
            {
                if (File.Exists(_path))
                    File.Delete(_path);
            }
            catch
            {
                /* ignore */
            }
        }
    }
}
