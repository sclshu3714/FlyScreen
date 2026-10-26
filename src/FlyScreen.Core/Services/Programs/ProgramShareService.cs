using FlyScreen.Services.Network;

namespace FlyScreen.Services.Programs;

public sealed record ProgramShareResult(int Reachable, int Sent);

public sealed class ProgramSharePayload
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Language { get; set; }
    public string Namespace { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Source { get; set; } = "";
    public string? ProjectFile { get; set; }
}

/// <summary>把程序发给已连接对端。同 Id 覆盖源码；仅同名不同 Id 才跳过。</summary>
public sealed class ProgramShareService
{
    private static readonly object Gate = new();

    public static ProgramShareService? Current { get; private set; }

    private readonly ConnectionManager _connections;
    private readonly ProgramCatalog _catalog;
    private readonly SynchronizationContext? _ui;

    private ProgramShareService(ConnectionManager connections, ProgramCatalog catalog, SynchronizationContext? ui)
    {
        _connections = connections;
        _catalog = catalog;
        _ui = ui;
        _connections.MessageReceived += OnMessage;
    }

    public static void Attach(ConnectionManager connections)
    {
        lock (Gate)
        {
            if (Current != null && ReferenceEquals(Current._connections, connections))
                return;
            Current = new ProgramShareService(connections, ProgramService.Instance.Catalog, SynchronizationContext.Current);
        }
    }

    public async Task<ProgramShareResult> ShareAsync(UserProgram prog)
    {
        var peers = _connections.GetConfiguredPeerStatuses()
            .Where(p => p.AnyAlive)
            .Select(p => p.Name)
            .ToList();
        if (peers.Count == 0)
            return new ProgramShareResult(0, 0);

        var payload = BuildPayload(prog);
        byte[] bytes;
        try
        {
            bytes = MessageCodec.JsonBytes(payload);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, $"共享「{prog.Name}」失败", prog.Id, prog.Name);
            return new ProgramShareResult(peers.Count, 0);
        }

        if (bytes.Length > MessageCodec.MaxPayload - 32)
        {
            AppLog.Error($"程序「{prog.Name}」过大，无法共享", prog.Id, prog.Name);
            return new ProgramShareResult(peers.Count, 0);
        }

        var sent = 0;
        foreach (var peer in peers)
        {
            if (await _connections.SendToAsync(peer, MessageType.ProgramShare, bytes).ConfigureAwait(false))
                sent++;
        }

        AppLog.Info($"已将程序「{prog.Name}」发送到 {sent} 台电脑", prog.Id, prog.Name);
        return new ProgramShareResult(peers.Count, sent);
    }

    private ProgramSharePayload BuildPayload(UserProgram prog)
    {
        string? project = null;
        if (prog.Language == ProgramLanguage.CSharp)
        {
            var path = Path.Combine(_catalog.GetFolder(prog), "program.csproj");
            if (File.Exists(path))
                project = File.ReadAllText(path);
        }

        return new ProgramSharePayload
        {
            Id = prog.Id,
            Name = prog.Name,
            Language = (int)prog.Language,
            Namespace = prog.Namespace,
            FileName = prog.FileName,
            Source = _catalog.ReadSource(prog),
            ProjectFile = project
        };
    }

    private void OnMessage(string peer, NetMessage msg)
    {
        if (msg.Type != MessageType.ProgramShare) return;
        ProgramSharePayload? payload;
        try
        {
            payload = MessageCodec.DecodeJson<ProgramSharePayload>(msg.Payload);
        }
        catch
        {
            return;
        }

        if (payload == null || string.IsNullOrWhiteSpace(payload.Name)) return;
        if (!Enum.IsDefined(typeof(ProgramLanguage), payload.Language)) return;

        void Apply()
        {
            try
            {
                // 同 Id：覆盖源码（修复「电脑改了脚本、手机仍跑旧版」）
                var byId = !string.IsNullOrWhiteSpace(payload.Id)
                    ? _catalog.Find(payload.Id)
                    : null;
                if (byId != null)
                {
                    if (!string.IsNullOrWhiteSpace(payload.FileName))
                        byId.FileName = Path.GetFileName(payload.FileName.Trim());
                    if (!string.IsNullOrWhiteSpace(payload.Namespace))
                        byId.Namespace = payload.Namespace.Trim();
                    _catalog.WriteSource(byId, payload.Source ?? "");
                    if (byId.Language == ProgramLanguage.CSharp && !string.IsNullOrWhiteSpace(payload.ProjectFile))
                    {
                        var csproj = Path.Combine(_catalog.GetFolder(byId), "program.csproj");
                        File.WriteAllText(csproj, payload.ProjectFile, new System.Text.UTF8Encoding(false));
                    }
                    AppLog.Info($"已更新共享程序「{byId.Name}」源码（来自 {peer}）", byId.Id, byId.Name);
                    return;
                }

                var existing = _catalog.FindDuplicate(payload.Id, payload.Name);
                if (existing != null)
                {
                    AppLog.Info($"已跳过重复程序「{payload.Name.Trim()}」（来自 {peer}）", existing.Id, existing.Name);
                    return;
                }

                var prog = new UserProgram
                {
                    Id = payload.Id,
                    Name = payload.Name,
                    Language = (ProgramLanguage)payload.Language,
                    Namespace = payload.Namespace,
                    FileName = payload.FileName
                };
                if (!_catalog.TryImportShared(prog, payload.Source, payload.ProjectFile))
                    AppLog.Info($"已跳过重复程序「{payload.Name.Trim()}」（来自 {peer}）", payload.Id, payload.Name.Trim());
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, $"接收共享程序「{payload.Name}」失败");
            }
        }

        if (_ui != null)
            _ui.Post(_ => Apply(), null);
        else
            Apply();
    }
}
