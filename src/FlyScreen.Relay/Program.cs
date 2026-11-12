using System.Net;
using FlyScreen.Relay.Admin;
using FlyScreen.Relay.Models;
using FlyScreen.Relay.Options;
using FlyScreen.Relay.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<RelayOptions>(builder.Configuration.GetSection(RelayOptions.SectionName));
var relayOpts = builder.Configuration.GetSection(RelayOptions.SectionName).Get<RelayOptions>() ?? new RelayOptions();

var edgeEp = ParseListen(relayOpts.EdgeListen, 8443);
var adminEp = ParseListen(relayOpts.AdminListen, 9080);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Listen(edgeEp.Address, edgeEp.Port);
    kestrel.Listen(adminEp.Address, adminEp.Port);
});

builder.Services.AddSingleton<RelayStats>();
builder.Services.AddSingleton<DeviceRegistry>();
builder.Services.AddSingleton<RelaySessionManager>();
builder.Services.AddSingleton<EdgeWebSocketHandler>();
builder.Services.AddHostedService<StunServer>();
builder.Services.AddHostedService<FlyScreenDiscoveryBridge>();
builder.Services.AddHostedService<FlyScreenTcpProbeServer>();
builder.Services.AddHostedService<MaintenanceService>();

var app = builder.Build();
var edgePort = edgeEp.Port;
var adminPort = adminEp.Port;

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseDefaultFiles();
app.UseStaticFiles();

bool IsAdmin(HttpContext ctx) => ctx.Connection.LocalPort == adminPort;
bool IsEdge(HttpContext ctx) => ctx.Connection.LocalPort == edgePort;

// ---- Admin API（管理口）----
app.MapGet("/api/health", (HttpContext ctx, IOptionsMonitor<RelayOptions> opt, DeviceRegistry devices, RelaySessionManager sessions, RelayStats stats) =>
{
    if (!IsAdmin(ctx)) return Results.NotFound();
    return Results.Json(new
    {
        ok = true,
        server = opt.CurrentValue.ServerName,
        startedAt = stats.StartedAt,
        uptimeSec = (long)(DateTimeOffset.UtcNow - stats.StartedAt).TotalSeconds,
        devices = devices.OnlineCount,
        rooms = devices.RoomCount,
        sessions = sessions.ActiveCount,
        relayBytes = stats.RelayBytes,
        signals = stats.SignalsForwarded
    });
});

app.MapGet("/api/devices", (HttpContext ctx, IOptionsMonitor<RelayOptions> opt, DeviceRegistry devices) =>
{
    if (!IsAdmin(ctx)) return Results.NotFound();
    if (!AdminAuth.IsAuthorized(ctx, opt.CurrentValue, write: false)) return AdminAuth.UnauthorizedResult();
    return Results.Json(new { devices = devices.ListAll() });
});

app.MapGet("/api/rooms", (HttpContext ctx, IOptionsMonitor<RelayOptions> opt, DeviceRegistry devices) =>
{
    if (!IsAdmin(ctx)) return Results.NotFound();
    if (!AdminAuth.IsAuthorized(ctx, opt.CurrentValue, write: false)) return AdminAuth.UnauthorizedResult();
    return Results.Json(new
    {
        rooms = devices.ListRooms().Select(r => new
        {
            roomId = r.RoomId,
            deviceCount = r.Count,
            devices = devices.ListRoom(r.RoomId)
        })
    });
});

app.MapGet("/api/sessions", (HttpContext ctx, IOptionsMonitor<RelayOptions> opt, RelaySessionManager sessions) =>
{
    if (!IsAdmin(ctx)) return Results.NotFound();
    if (!AdminAuth.IsAuthorized(ctx, opt.CurrentValue, write: false)) return AdminAuth.UnauthorizedResult();
    return Results.Json(new
    {
        sessions = sessions.List().Select(s => new
        {
            s.SessionId,
            s.RoomId,
            s.DeviceA,
            s.DeviceB,
            s.CreatedAt,
            s.LastActivity,
            s.BytesAtoB,
            s.BytesBtoA
        })
    });
});

app.MapGet("/api/stats", (HttpContext ctx, IOptionsMonitor<RelayOptions> opt, DeviceRegistry devices, RelaySessionManager sessions, RelayStats stats) =>
{
    if (!IsAdmin(ctx)) return Results.NotFound();
    if (!AdminAuth.IsAuthorized(ctx, opt.CurrentValue, write: false)) return AdminAuth.UnauthorizedResult();
    return Results.Json(new
    {
        server = opt.CurrentValue.ServerName,
        startedAt = stats.StartedAt,
        devicesOnline = devices.OnlineCount,
        rooms = devices.RoomCount,
        sessionsActive = sessions.ActiveCount,
        sessionsOpened = stats.SessionsOpened,
        sessionsClosed = stats.SessionsClosed,
        relayBytes = stats.RelayBytes,
        signalsForwarded = stats.SignalsForwarded
    });
});

app.MapDelete("/api/devices/{deviceId}", async (string deviceId, HttpContext ctx, IOptionsMonitor<RelayOptions> opt, DeviceRegistry devices) =>
{
    if (!IsAdmin(ctx)) return Results.NotFound();
    if (!AdminAuth.IsAuthorized(ctx, opt.CurrentValue, write: true)) return AdminAuth.UnauthorizedResult();
    await devices.KickAsync(deviceId).ConfigureAwait(false);
    return Results.Json(new { ok = true, kicked = deviceId });
});

app.MapPost("/api/sessions/{sessionId}/close", (string sessionId, HttpContext ctx, IOptionsMonitor<RelayOptions> opt, RelaySessionManager sessions) =>
{
    if (!IsAdmin(ctx)) return Results.NotFound();
    if (!AdminAuth.IsAuthorized(ctx, opt.CurrentValue, write: true)) return AdminAuth.UnauthorizedResult();
    sessions.Close(sessionId, "admin");
    return Results.Json(new { ok = true, closed = sessionId });
});

// ---- Edge（注册 / HTTP 发现 / 信令 / 中继）----
app.MapGet("/health", (HttpContext ctx) =>
    IsEdge(ctx) ? Results.Json(new { ok = true }) : Results.NotFound());

app.MapGet("/v1/info", (HttpContext ctx, IOptionsMonitor<RelayOptions> opt, DeviceRegistry devices, RelaySessionManager sessions) =>
{
    if (!IsEdge(ctx)) return Results.NotFound();
    var o = opt.CurrentValue;
    var publicUrl = string.IsNullOrWhiteSpace(o.PublicEdgeUrl)
        ? $"{ctx.Request.Scheme}://{ctx.Request.Host}"
        : o.PublicEdgeUrl.TrimEnd('/');
    return Results.Json(new
    {
        server = o.ServerName,
        version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0",
        edgeUrl = publicUrl,
        wsPath = "/ws",
        stunUdpPort = o.StunUdpPort,
        peerTcpPort = o.PeerTcpPort,
        discoveryUdpPort = o.DiscoveryUdpPort,
        devicesOnline = devices.OnlineCount,
        sessionsActive = sessions.ActiveCount,
        connectAdvice = new ConnectAdvice()
    });
});

app.MapGet("/v1/devices", (HttpContext ctx, DeviceRegistry devices) =>
{
    if (!IsEdge(ctx)) return Results.NotFound();
    var roomKey = ctx.Request.Query["roomKey"].FirstOrDefault()
                  ?? ctx.Request.Headers["X-Room-Key"].FirstOrDefault()
                  ?? "";
    var roomId = DeviceRegistry.HashRoomKey(roomKey);
    var list = devices.ListRoom(roomId);
    return Results.Json(new { roomId, count = list.Count, devices = list });
});

app.MapGet("/v1/discover", (HttpContext ctx, DeviceRegistry devices) =>
{
    if (!IsEdge(ctx)) return Results.NotFound();
    var list = devices.ListAll();
    return Results.Json(new { count = list.Count, devices = list });
});

var wsHandler = app.Services.GetRequiredService<EdgeWebSocketHandler>();
app.Map("/ws", async (HttpContext ctx) =>
{
    if (!IsEdge(ctx))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await wsHandler.HandleAsync(ctx).ConfigureAwait(false);
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FlyScreen.Relay");
    log.LogInformation(
        "玄鸟中继已启动：边缘 {Edge}（WS/HTTP），管理 {Admin}，Peer TCP {Peer}，STUN UDP {Stun}，发现 UDP {Disco}",
        relayOpts.EdgeListen,
        relayOpts.AdminListen,
        relayOpts.PeerTcpPort,
        relayOpts.StunUdpPort,
        relayOpts.DiscoveryUdpPort);
});

await app.RunAsync().ConfigureAwait(false);

static IPEndPoint ParseListen(string? raw, int defaultPort)
{
    raw = (raw ?? "").Trim();
    if (string.IsNullOrWhiteSpace(raw))
        return new IPEndPoint(IPAddress.Any, defaultPort);

    if (IPEndPoint.TryParse(raw, out var ep))
        return ep;

    var idx = raw.LastIndexOf(':');
    if (idx > 0 && int.TryParse(raw[(idx + 1)..], out var port) && port is > 0 and <= 65535)
    {
        var host = raw[..idx];
        if (host is "0.0.0.0" or "*")
            return new IPEndPoint(IPAddress.Any, port);
        if (host is "::" or "[::]")
            return new IPEndPoint(IPAddress.IPv6Any, port);
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
            return new IPEndPoint(ip, port);
        return new IPEndPoint(IPAddress.Any, port);
    }

    return new IPEndPoint(IPAddress.Any, defaultPort);
}
