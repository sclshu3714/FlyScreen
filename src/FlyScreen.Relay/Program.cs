using System.Net;
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

builder.Services.AddSingleton(sp =>
{
    var env = sp.GetRequiredService<IHostEnvironment>();
    var opt = sp.GetRequiredService<IOptions<RelayOptions>>().Value;
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("FlyScreen.Relay.Database");
    return RelayStore.Open(opt.Database, env.ContentRootPath, log);
});
builder.Services.AddSingleton<RelayStats>();
builder.Services.AddSingleton<DeviceRegistry>();
builder.Services.AddSingleton<AdminCatalog>();
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
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
});

bool IsAdmin(HttpContext ctx) => ctx.Connection.LocalPort == adminPort;
bool IsEdge(HttpContext ctx) => ctx.Connection.LocalPort == edgePort;

// ---- Admin API（管理口，页面直接读取，不再使用管理令牌）----
app.MapGet("/api/health", (HttpContext ctx, IOptionsMonitor<RelayOptions> opt, DeviceRegistry devices, AdminCatalog catalog, RelaySessionManager sessions, RelayStats stats) =>
{
    if (!IsAdmin(ctx)) return Results.NotFound();
    var rooms = catalog.ListRooms(includeDevices: false);
    return Results.Json(new
    {
        ok = true,
        server = opt.CurrentValue.ServerName,
        startedAt = stats.StartedAt,
        uptimeSec = (long)(DateTimeOffset.UtcNow - stats.StartedAt).TotalSeconds,
        devices = devices.OnlineCount,
        registeredDevices = rooms.Sum(r => r.DeviceCount),
        rooms = rooms.Count,
        sessions = sessions.ActiveCount,
        relayBytes = stats.RelayBytes,
        signals = stats.SignalsForwarded
    });
});

app.MapGet("/api/dashboard", (HttpContext ctx, IOptionsMonitor<RelayOptions> opt, AdminCatalog catalog, RelaySessionManager sessions, RelayStats stats) =>
{
    if (!IsAdmin(ctx)) return Results.NotFound();
    var rooms = catalog.ListRooms(includeDevices: true);
    var names = rooms.SelectMany(r => r.Devices).ToDictionary(d => d.Id, d => d.Name, StringComparer.OrdinalIgnoreCase);
    var roomNo = rooms.ToDictionary(r => r.RoomId, r => r.RoomNo, StringComparer.Ordinal);
    string NameOf(string id) => names.TryGetValue(id, out var n) && !string.IsNullOrWhiteSpace(n) ? n : id;
    return Results.Json(new
    {
        server = opt.CurrentValue.ServerName,
        startedAt = stats.StartedAt,
        uptimeSec = (long)(DateTimeOffset.UtcNow - stats.StartedAt).TotalSeconds,
        registeredRooms = rooms.Count,
        registeredDevices = rooms.Sum(r => r.DeviceCount),
        onlineDevices = rooms.Sum(r => r.OnlineCount),
        sessions = sessions.ActiveCount,
        relayBytes = stats.RelayBytes,
        signals = stats.SignalsForwarded,
        rooms = rooms.Select(r => new
        {
            r.RoomId,
            r.RoomNo,
            r.CreatedAt,
            r.DeviceCount,
            r.OnlineCount,
            r.LastSeen
        }),
        sessionList = sessions.List().Select(s => new
        {
            s.SessionId,
            s.RoomId,
            roomNo = roomNo.TryGetValue(s.RoomId, out var no) ? no : s.RoomId,
            s.DeviceA,
            deviceAName = NameOf(s.DeviceA),
            s.DeviceB,
            deviceBName = NameOf(s.DeviceB),
            s.CreatedAt,
            s.LastActivity,
            s.BytesAtoB,
            s.BytesBtoA
        })
    });
});

app.MapGet("/api/rooms", (HttpContext ctx, AdminCatalog catalog) =>
{
    if (!IsAdmin(ctx)) return Results.NotFound();
    return Results.Json(new { rooms = catalog.ListRooms(includeDevices: false) });
});

app.MapGet("/api/rooms/{roomId}", (string roomId, HttpContext ctx, AdminCatalog catalog) =>
{
    if (!IsAdmin(ctx)) return Results.NotFound();
    var room = catalog.FindRoom(roomId);
    return room is null
        ? Results.Json(new { error = "not_found", message = "房间不存在" }, statusCode: StatusCodes.Status404NotFound)
        : Results.Json(room);
});

app.MapDelete("/api/devices/{deviceId}", async (string deviceId, HttpContext ctx, DeviceRegistry devices) =>
{
    if (!IsAdmin(ctx)) return Results.NotFound();
    if (!devices.TryGetByDeviceId(deviceId, out var conn) || conn is null)
        return Results.Json(new { ok = false, error = "not_found" }, statusCode: StatusCodes.Status404NotFound);
    await devices.KickAsync(deviceId).ConfigureAwait(false);
    return Results.Json(new { ok = true, kicked = deviceId });
});

app.MapPost("/api/sessions/{sessionId}/close", (string sessionId, HttpContext ctx, RelaySessionManager sessions) =>
{
    if (!IsAdmin(ctx)) return Results.NotFound();
    if (!sessions.TryGet(sessionId, out var session) || session is null)
        return Results.Json(new { ok = false, error = "not_found" }, statusCode: StatusCodes.Status404NotFound);
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
