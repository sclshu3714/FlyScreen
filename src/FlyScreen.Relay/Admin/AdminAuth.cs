using FlyScreen.Relay.Options;

namespace FlyScreen.Relay.Admin;

public static class AdminAuth
{
    public const string HeaderName = "X-Admin-Token";

    public static bool IsAuthorized(HttpContext ctx, RelayOptions opts, bool write)
    {
        if (!write && opts.AllowAnonymousAdminRead)
            return true;

        var token = opts.AdminToken ?? "";
        var provided = ctx.Request.Headers[HeaderName].FirstOrDefault()
                       ?? ctx.Request.Query["token"].FirstOrDefault()
                       ?? ExtractBearer(ctx.Request.Headers.Authorization.FirstOrDefault());

        return !string.IsNullOrEmpty(provided)
               && !string.IsNullOrEmpty(token)
               && string.Equals(provided, token, StringComparison.Ordinal);
    }

    private static string? ExtractBearer(string? authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization)) return null;
        const string prefix = "Bearer ";
        return authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? authorization[prefix.Length..].Trim()
            : null;
    }

    public static IResult UnauthorizedResult()
        => Results.Json(
            new { error = "unauthorized", message = "需要有效的管理令牌（头 X-Admin-Token 或 ?token=）" },
            statusCode: StatusCodes.Status401Unauthorized);
}
