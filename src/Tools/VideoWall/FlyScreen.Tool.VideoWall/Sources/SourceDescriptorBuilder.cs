using FlyScreen.Tool.VideoWall.Contracts;

namespace FlyScreen.Tool.VideoWall.Sources;

/// <summary>由表单字段值组装 <see cref="VideoSourceDescriptor"/>（UI 卫星共用）。</summary>
public static class SourceDescriptorBuilder
{
    public static VideoSourceDescriptor? TryBuild(
        VideoSourceKind kind,
        IReadOnlyDictionary<string, string?> values,
        CredentialStore credentials,
        out string? error)
    {
        error = null;
        var rawAddress = kind switch
        {
            VideoSourceKind.LocalFile => values.GetValueOrDefault("path"),
            _ => values.GetValueOrDefault("address")
        };

        if (string.IsNullOrWhiteSpace(rawAddress))
        {
            error = "地址不能为空";
            return null;
        }

        var parse = VideoEndpointParser.ParseAndNormalize(rawAddress);
        if (!parse.Ok || parse.Endpoint is null)
        {
            error = parse.Message ?? "地址无法解析";
            return null;
        }

        string? credRef = null;
        var auth = values.GetValueOrDefault("auth") ?? SourceFormSchema.AuthNone;
        if (auth != SourceFormSchema.AuthNone)
        {
            var user = values.GetValueOrDefault("username")?.Trim();
            var pass = values.GetValueOrDefault("password") ?? "";
            if (string.IsNullOrWhiteSpace(user))
            {
                error = "请填写用户名";
                return null;
            }

            credRef = credentials.Save(null, user, pass, auth);
        }

        int? bufferMs = int.TryParse(values.GetValueOrDefault("bufferMs"), out var buf) ? buf : null;
        var transport = values.GetValueOrDefault("transport");

        var endpoint = parse.Endpoint with
        {
            Transport = string.IsNullOrWhiteSpace(transport) ? parse.Endpoint.Transport : transport,
            BufferMs = bufferMs ?? parse.Endpoint.BufferMs,
            CredentialRef = credRef ?? parse.Endpoint.CredentialRef
        };

        var name = values.GetValueOrDefault("name")?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = Path.GetFileName(endpoint.Address) ?? "源";

        var decode = values.GetValueOrDefault("decode") ?? "auto";
        var groupId = values.GetValueOrDefault("groupId");
        var note = values.GetValueOrDefault("note");

        return new VideoSourceDescriptor
        {
            Id = VideoSourceCatalog.NewId(),
            Name = name,
            Kind = kind,
            Origin = VideoSourceOrigin.Fixed,
            Endpoint = endpoint,
            GroupId = string.IsNullOrWhiteSpace(groupId) ? null : groupId,
            DecodePreference = decode,
            Note = string.IsNullOrWhiteSpace(note) ? null : note,
            ValidationState = SourceValidationState.Unknown
        };
    }
}
