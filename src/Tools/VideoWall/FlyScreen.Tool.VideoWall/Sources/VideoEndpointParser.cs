using System.Text;
using System.Text.RegularExpressions;
using FlyScreen.Tool.VideoWall.Contracts;

namespace FlyScreen.Tool.VideoWall.Sources;

/// <summary>地址解析与归一化结果（docs/video-wall-sources.md §6）。</summary>
public sealed record ParseResult
{
  public bool Ok { get; init; }
  public VideoEndpoint? Endpoint { get; init; }
  public string? User { get; init; }
  public string? Password { get; init; }
  public VideoErrorCode ErrorCode { get; init; } = VideoErrorCode.None;
  public string? Message { get; init; }
  public bool UsedDefaultPort { get; init; }
  public bool IsSignedUrl { get; init; }
  public bool IsMulticast { get; init; }
}

/// <summary>
/// 视频地址解析、归一化与脱敏。实现 sources.md §6 全部规则；重复地址去重由 <see cref="VideoSourceCatalog"/> 负责。
/// </summary>
public static partial class VideoEndpointParser
{
  private const int MaxAddressLength = 2048;

  private static readonly string[] UnrecognizedExamples =
  [
      "rtsp://192.168.1.64:554/Streaming/Channels/101",
      "https://cdn.example.com/live.m3u8",
      @"D:\video\sample.mp4"
  ];

  private static readonly string[] SignedQueryKeys =
      ["token", "sign", "key", "auth", "expires"];

  private static readonly Dictionary<string, int> DefaultPorts = new(StringComparer.OrdinalIgnoreCase)
  {
      ["rtsp"] = 554,
      ["rtmp"] = 1935,
      ["rtmps"] = 443,
      ["http"] = 80,
      ["https"] = 443,
      ["srt"] = 9000
  };

  /// <summary>解析原始输入并归一化为 <see cref="VideoEndpoint"/>；内嵌凭据抽出到 User/Password。</summary>
  public static ParseResult ParseAndNormalize(string raw)
  {
      if (raw is null)
          return Fail(VideoErrorCode.SourceUnsupported, "地址不能为空");

      var trimmed = raw.Trim();
      if (trimmed.Length == 0)
          return Fail(VideoErrorCode.SourceUnsupported, "地址不能为空");

      if (trimmed.Length > MaxAddressLength)
          return Fail(VideoErrorCode.SourceUnsupported, "地址过长（超过 2048 字符），请检查是否误粘贴了整段网页");

      var working = NormalizeFullWidthSchemeSeparators(trimmed);

      string? user = null;
      string? password = null;
      var isMulticast = false;
      var usedDefaultPort = false;

      if (TryExtractUserInfo(ref working, out user, out password))
      {
          // 凭据已从地址剔除
      }

      string scheme;
      string addressBody;

      if (TrySplitScheme(working, out var schemePart, out var rest))
      {
          scheme = schemePart.ToLowerInvariant();
          addressBody = rest;
      }
      else if (IsLikelyWindowsOrUncPath(working) || File.Exists(working))
      {
          scheme = "file";
          addressBody = working;
          return BuildFileResult(working, user, password, isMulticast, usedDefaultPort);
      }
      else if (LooksLikeRelativeOrUnknownPath(working))
      {
          if (File.Exists(Path.GetFullPath(working)))
          {
              scheme = "file";
              var full = Path.GetFullPath(working);
              return BuildFileResult(full, user, password, isMulticast, usedDefaultPort);
          }

          return Fail(
              VideoErrorCode.SourceUnsupported,
              "无法识别的地址。示例：\n• " + string.Join("\n• ", UnrecognizedExamples));
      }
      else
      {
          return Fail(
              VideoErrorCode.SourceUnsupported,
              "无法识别的地址。示例：\n• " + string.Join("\n• ", UnrecognizedExamples));
      }

      if (string.Equals(scheme, "file", StringComparison.OrdinalIgnoreCase))
          return BuildFileResult(addressBody, user, password, isMulticast, usedDefaultPort);

      if (IsMulticastAddress(scheme, addressBody))
          isMulticast = true;

      var protocol = DetectProtocol(scheme, addressBody);
      var normalizedAddress = RebuildUriAddress(scheme, addressBody, protocol, ref usedDefaultPort);
      var isSigned = HasSignedQueryParameters(normalizedAddress);

      var endpoint = new VideoEndpoint
      {
          Protocol = protocol,
          Address = normalizedAddress
      };

      return new ParseResult
      {
          Ok = true,
          Endpoint = endpoint,
          User = user,
          Password = password,
          UsedDefaultPort = usedDefaultPort,
          IsSignedUrl = isSigned,
          IsMulticast = isMulticast
      };
  }

  /// <summary>从 scheme 或路径扩展名推断协议标识。</summary>
  public static string DetectProtocol(string scheme, string addressPath)
  {
      var lower = scheme.ToLowerInvariant();
      if (lower is "hls" or "dash")
          return lower;

      var path = addressPath;
      var queryIdx = path.IndexOf('?');
      if (queryIdx >= 0) path = path[..queryIdx];

      if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
          return "hls";
      if (path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
          return "dash";

      return lower switch
      {
          "rtsp" or "rtmp" or "rtmps" or "http" or "https" or "srt" or "udp" or "rtp" or "file" => lower,
          _ => lower
      };
  }

  /// <summary>日志/UI 用脱敏地址：内嵌凭据显示为 <c>***</c>。</summary>
  public static string RedactAddress(string address)
  {
      if (string.IsNullOrEmpty(address)) return address;

      var at = address.IndexOf('@');
      if (at <= 0) return RedactSignedQueryValues(address);

      var schemeEnd = address.IndexOf("://", StringComparison.Ordinal);
      if (schemeEnd < 0 || at <= schemeEnd + 3)
          return RedactSignedQueryValues(address);

      var scheme = address[..(schemeEnd + 3)];
      var afterAt = address[(at + 1)..];
      return RedactSignedQueryValues(scheme + "***@" + afterAt);
  }

  private static ParseResult BuildFileResult(
      string path,
      string? user,
      string? password,
      bool isMulticast,
      bool usedDefaultPort)
  {
      var endpoint = new VideoEndpoint
      {
          Protocol = "file",
          Address = path
      };

      return new ParseResult
      {
          Ok = true,
          Endpoint = endpoint,
          User = user,
          Password = password,
          IsMulticast = isMulticast,
          UsedDefaultPort = usedDefaultPort
      };
  }

  private static ParseResult Fail(VideoErrorCode code, string message)
      => new() { Ok = false, ErrorCode = code, Message = message };

  private static string NormalizeFullWidthSchemeSeparators(string input)
  {
      var sb = new StringBuilder(input.Length);
      var inQuery = false;
      for (var i = 0; i < input.Length; i++)
      {
          var c = input[i];
          if (c == '?' && !inQuery) inQuery = true;

          if (!inQuery && (c == '\uFF1A' || c == '\uFF0F'))
              sb.Append(c == '\uFF1A' ? ':' : '/');
          else
              sb.Append(c);
      }

      return sb.ToString();
  }

  private static bool TrySplitScheme(string working, out string scheme, out string rest)
  {
      var idx = working.IndexOf("://", StringComparison.Ordinal);
      if (idx <= 0)
      {
          scheme = "";
          rest = working;
          return false;
      }

      scheme = working[..idx];
      rest = working[(idx + 3)..];
      return !string.IsNullOrWhiteSpace(scheme);
  }

  private static bool IsLikelyWindowsOrUncPath(string s)
      => WindowsDrivePath().IsMatch(s) || s.StartsWith(@"\\", StringComparison.Ordinal);

  private static bool LooksLikeRelativeOrUnknownPath(string s)
      => !s.Contains("://", StringComparison.Ordinal)
         && (s.Contains('\\') || s.Contains('/') || s.Contains('.'));

  private static bool TryExtractUserInfo(ref string working, out string? user, out string? password)
  {
      user = null;
      password = null;

      var schemeIdx = working.IndexOf("://", StringComparison.Ordinal);
      if (schemeIdx < 0) return false;

      var authorityStart = schemeIdx + 3;
      var at = working.IndexOf('@', authorityStart);
      if (at < 0) return false;

      var slash = working.IndexOf('/', authorityStart);
      if (slash >= 0 && at > slash) return false;

      var userInfo = working[authorityStart..at];
      var colon = userInfo.IndexOf(':');
      if (colon >= 0)
      {
          user = Uri.UnescapeDataString(userInfo[..colon]);
          password = Uri.UnescapeDataString(userInfo[(colon + 1)..]);
      }
      else
      {
          user = Uri.UnescapeDataString(userInfo);
      }

      working = working[..authorityStart] + working[(at + 1)..];
      return true;
  }

  private static bool IsMulticastAddress(string scheme, string addressBody)
  {
      if (!scheme.Equals("udp", StringComparison.OrdinalIgnoreCase)
          && !scheme.Equals("rtp", StringComparison.OrdinalIgnoreCase))
          return false;

      return addressBody.StartsWith('@')
             || addressBody.Contains("@239.", StringComparison.Ordinal);
  }

  private static bool HasSignedQueryParameters(string uri)
  {
      var q = uri.IndexOf('?');
      if (q < 0) return false;

      var query = uri[(q + 1)..];
      foreach (var segment in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
      {
          var eq = segment.IndexOf('=');
          var key = eq >= 0 ? segment[..eq] : segment;
          if (SignedQueryKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
              return true;
      }

      return false;
  }

  private static string RedactSignedQueryValues(string uri)
  {
      var q = uri.IndexOf('?');
      if (q < 0) return uri;

      var path = uri[..(q + 1)];
      var query = uri[(q + 1)..];
      var parts = new List<string>();
      foreach (var segment in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
      {
          var eq = segment.IndexOf('=');
          if (eq < 0)
          {
              parts.Add(segment);
              continue;
          }

          var key = segment[..eq];
          if (SignedQueryKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
              parts.Add(key + "=***");
          else
              parts.Add(segment);
      }

      return path + string.Join("&", parts);
  }

  private static string RebuildUriAddress(string scheme, string addressBody, string protocol, ref bool usedDefaultPort)
  {
      var full = $"{scheme}://{addressBody}";

      if (!DefaultPorts.TryGetValue(scheme, out var defaultPort))
          return full;

      if (!Uri.TryCreate(full, UriKind.Absolute, out var uri))
          return full;

      if (uri.IsDefaultPort && defaultPort is not (80 or 443))
      {
          // Uri 会把省略端口当默认；对 rtsp/rtmp/srt 需显式写出协议默认口
          var builder = new UriBuilder(uri) { Port = defaultPort };
          usedDefaultPort = true;
          return builder.Uri.ToString().TrimEnd('/');
      }

      if (uri.Port == defaultPort && !addressBody.Contains(':', StringComparison.Ordinal))
          usedDefaultPort = true;

      return uri.ToString();
  }

  [GeneratedRegex(@"^[a-zA-Z]:\\")]
  private static partial Regex WindowsDrivePath();
}