using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using FlyScreen.Relay.Models;
using FlyScreen.Relay.Options;
using Microsoft.Data.Sqlite;
using MySqlConnector;

namespace FlyScreen.Relay.Services;

public sealed class StoredRoom
{
    public required string RoomId { get; init; }
    public required string RoomNo { get; init; }
    public required string FirstDeviceId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed class StoredDevice
{
    public required string DeviceId { get; init; }
    public required string RoomId { get; init; }
    public required string Name { get; init; }
    public string? Platform { get; init; }
    public string? PublicKey { get; init; }
    public int? TcpPort { get; init; }
    public string? RemoteAddress { get; init; }
    public List<AddressCandidate>? Candidates { get; init; }
    public required DateTimeOffset RegisteredAt { get; init; }
    public required DateTimeOffset LastSeen { get; init; }
}

/// <summary>注册房间与设备。默认内嵌 SQLite；仅当配置明确关闭内嵌库时使用 MySQL。</summary>
public sealed class RelayStore : IDisposable
{
    private enum ProviderKind { Sqlite, MySql }

    private readonly DbConnection _conn;
    private readonly ProviderKind _kind;
    private readonly Lock _gate = new();

    private RelayStore(DbConnection conn, ProviderKind kind)
    {
        _conn = conn;
        _kind = kind;
    }

    public string Description { get; private set; } = "";

    public static RelayStore Open(DatabaseOptions options, string contentRoot, ILogger log)
    {
        var (kind, connectionString, description) = Resolve(options, contentRoot);
        DbConnection conn = kind switch
        {
            ProviderKind.Sqlite => new SqliteConnection(connectionString),
            ProviderKind.MySql => new MySqlConnection(connectionString),
            _ => throw new InvalidOperationException("未知数据库类型")
        };
        conn.Open();
        var store = new RelayStore(conn, kind) { Description = description };
        store.EnsureSchema();
        log.LogInformation("注册库已打开：{Database}", description);
        return store;
    }

    public string EnsureRoom(string roomId, string roomNo, string firstDeviceId)
    {
        var created = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        lock (_gate)
        {
            if (_kind == ProviderKind.Sqlite)
            {
                Exec("""
                    INSERT INTO rooms (room_id, room_no, first_device_id, created_at)
                    VALUES (@roomId, @roomNo, @deviceId, @created)
                    ON CONFLICT(room_id) DO NOTHING
                    """, cmd =>
                {
                    P(cmd, "@roomId", roomId);
                    P(cmd, "@roomNo", roomNo);
                    P(cmd, "@deviceId", firstDeviceId);
                    P(cmd, "@created", created);
                });
            }
            else
            {
                Exec("""
                    INSERT INTO rooms (room_id, room_no, first_device_id, created_at)
                    VALUES (@roomId, @roomNo, @deviceId, @created)
                    ON DUPLICATE KEY UPDATE room_id = room_id
                    """, cmd =>
                {
                    P(cmd, "@roomId", roomId);
                    P(cmd, "@roomNo", roomNo);
                    P(cmd, "@deviceId", firstDeviceId);
                    P(cmd, "@created", created);
                });
            }

            return ScalarString("SELECT room_no FROM rooms WHERE room_id = @roomId", cmd => P(cmd, "@roomId", roomId))
                   ?? roomNo;
        }
    }

    public void UpsertDevice(DeviceConnection conn)
    {
        var now = conn.LastSeen.ToString("o", CultureInfo.InvariantCulture);
        var registered = conn.ConnectedAt.ToString("o", CultureInfo.InvariantCulture);
        var candidates = conn.Candidates.Count == 0
            ? null
            : JsonSerializer.Serialize(conn.Candidates, Wire.JsonOptions);

        lock (_gate)
        {
            if (_kind == ProviderKind.Sqlite)
            {
                Exec("""
                    INSERT INTO devices (
                      device_id, room_id, name, platform, public_key, tcp_port,
                      remote_address, candidates_json, registered_at, last_seen)
                    VALUES (
                      @deviceId, @roomId, @name, @platform, @publicKey, @tcpPort,
                      @remote, @candidates, @registered, @seen)
                    ON CONFLICT(device_id) DO UPDATE SET
                      room_id = excluded.room_id,
                      name = excluded.name,
                      platform = excluded.platform,
                      public_key = excluded.public_key,
                      tcp_port = excluded.tcp_port,
                      remote_address = excluded.remote_address,
                      candidates_json = excluded.candidates_json,
                      last_seen = excluded.last_seen
                    """, cmd => BindDevice(cmd, conn, candidates, registered, now));
            }
            else
            {
                Exec("""
                    INSERT INTO devices (
                      device_id, room_id, name, platform, public_key, tcp_port,
                      remote_address, candidates_json, registered_at, last_seen)
                    VALUES (
                      @deviceId, @roomId, @name, @platform, @publicKey, @tcpPort,
                      @remote, @candidates, @registered, @seen)
                    ON DUPLICATE KEY UPDATE
                      room_id = VALUES(room_id),
                      name = VALUES(name),
                      platform = VALUES(platform),
                      public_key = VALUES(public_key),
                      tcp_port = VALUES(tcp_port),
                      remote_address = VALUES(remote_address),
                      candidates_json = VALUES(candidates_json),
                      last_seen = VALUES(last_seen)
                    """, cmd => BindDevice(cmd, conn, candidates, registered, now));
            }
        }
    }

    public void Touch(string deviceId, DateTimeOffset seen)
    {
        lock (_gate)
        {
            Exec("UPDATE devices SET last_seen = @seen WHERE device_id = @deviceId", cmd =>
            {
                P(cmd, "@seen", seen.ToString("o", CultureInfo.InvariantCulture));
                P(cmd, "@deviceId", deviceId);
            });
        }
    }

    public IReadOnlyList<StoredRoom> ListRooms()
    {
        lock (_gate)
        {
            var list = new List<StoredRoom>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT room_id, room_no, first_device_id, created_at FROM rooms ORDER BY created_at";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new StoredRoom
                {
                    RoomId = reader.GetString(0),
                    RoomNo = reader.GetString(1),
                    FirstDeviceId = reader.GetString(2),
                    CreatedAt = ParseTime(reader.GetString(3))
                });
            }
            return list;
        }
    }

    public IReadOnlyList<StoredDevice> ListDevices()
    {
        lock (_gate)
        {
            var list = new List<StoredDevice>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT device_id, room_id, name, platform, public_key, tcp_port,
                       remote_address, candidates_json, registered_at, last_seen
                FROM devices
                ORDER BY name
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add(ReadDevice(reader));
            return list;
        }
    }

    public bool DeleteDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        lock (_gate)
        {
            return ExecCount("DELETE FROM devices WHERE device_id = @deviceId", cmd => P(cmd, "@deviceId", deviceId)) > 0;
        }
    }

    /// <summary>删除房间及其下全部设备注册记录。</summary>
    public bool DeleteRoom(string roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId)) return false;
        lock (_gate)
        {
            var nDev = ExecCount("DELETE FROM devices WHERE room_id = @roomId", cmd => P(cmd, "@roomId", roomId));
            var nRoom = ExecCount("DELETE FROM rooms WHERE room_id = @roomId", cmd => P(cmd, "@roomId", roomId));
            return nDev > 0 || nRoom > 0;
        }
    }

    /// <summary>清空全部房间与设备注册记录。</summary>
    public int ClearAll()
    {
        lock (_gate)
        {
            var nDev = ExecCount("DELETE FROM devices");
            var nRoom = ExecCount("DELETE FROM rooms");
            return nDev + nRoom;
        }
    }

    public void Dispose() => _conn.Dispose();

    private void EnsureSchema()
    {
        if (_kind == ProviderKind.Sqlite)
        {
            Exec("""
                CREATE TABLE IF NOT EXISTS rooms (
                  room_id TEXT PRIMARY KEY,
                  room_no TEXT NOT NULL,
                  first_device_id TEXT NOT NULL,
                  created_at TEXT NOT NULL
                )
                """);
            Exec("""
                CREATE TABLE IF NOT EXISTS devices (
                  device_id TEXT PRIMARY KEY,
                  room_id TEXT NOT NULL,
                  name TEXT NOT NULL,
                  platform TEXT NULL,
                  public_key TEXT NULL,
                  tcp_port INTEGER NULL,
                  remote_address TEXT NULL,
                  candidates_json TEXT NULL,
                  registered_at TEXT NOT NULL,
                  last_seen TEXT NOT NULL
                )
                """);
            Exec("CREATE INDEX IF NOT EXISTS ix_devices_room ON devices(room_id)");
            return;
        }

        Exec("""
            CREATE TABLE IF NOT EXISTS rooms (
              room_id VARCHAR(64) NOT NULL PRIMARY KEY,
              room_no VARCHAR(256) NOT NULL,
              first_device_id VARCHAR(64) NOT NULL,
              created_at VARCHAR(40) NOT NULL
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """);
        Exec("""
            CREATE TABLE IF NOT EXISTS devices (
              device_id VARCHAR(64) NOT NULL PRIMARY KEY,
              room_id VARCHAR(64) NOT NULL,
              name VARCHAR(256) NOT NULL,
              platform VARCHAR(64) NULL,
              public_key VARCHAR(512) NULL,
              tcp_port INT NULL,
              remote_address VARCHAR(256) NULL,
              candidates_json TEXT NULL,
              registered_at VARCHAR(40) NOT NULL,
              last_seen VARCHAR(40) NOT NULL,
              KEY ix_devices_room (room_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """);
    }

    private static (ProviderKind Kind, string ConnectionString, string Description) Resolve(DatabaseOptions options, string contentRoot)
    {
        if (options.UseEmbedded)
        {
            var rel = string.IsNullOrWhiteSpace(options.EmbeddedPath) ? "data/relay.db" : options.EmbeddedPath.Trim();
            var full = Path.IsPathRooted(rel) ? rel : Path.GetFullPath(Path.Combine(contentRoot, rel));
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            return (ProviderKind.Sqlite, $"Data Source={full};Mode=ReadWriteCreate;Cache=Shared", $"内嵌 SQLite {full}");
        }

        var provider = (options.Provider ?? "").Trim();
        var cs = (options.ConnectionString ?? "").Trim();
        if (provider.Length == 0 || cs.Length == 0)
        {
            throw new InvalidOperationException(
                "Relay:Database:UseEmbedded 已设为 false，必须同时配置 Provider（mysql 或 mariadb）与 ConnectionString。未配置时不会回退到内嵌库。");
        }

        if (provider.Equals("mysql", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("mariadb", StringComparison.OrdinalIgnoreCase))
            return (ProviderKind.MySql, cs, $"外部 MySQL ({provider})");

        throw new InvalidOperationException(
            $"不支持的外部数据库 Provider「{provider}」。关闭内嵌库后当前支持 mysql、mariadb。");
    }

    private static void BindDevice(DbCommand cmd, DeviceConnection conn, string? candidates, string registered, string seen)
    {
        P(cmd, "@deviceId", conn.DeviceId);
        P(cmd, "@roomId", conn.RoomId);
        P(cmd, "@name", conn.Name);
        P(cmd, "@platform", conn.Platform);
        P(cmd, "@publicKey", conn.PublicKey);
        P(cmd, "@tcpPort", conn.TcpPort);
        P(cmd, "@remote", conn.RemoteAddress);
        P(cmd, "@candidates", candidates);
        P(cmd, "@registered", registered);
        P(cmd, "@seen", seen);
    }

    private static StoredDevice ReadDevice(DbDataReader reader)
    {
        List<AddressCandidate>? candidates = null;
        if (!reader.IsDBNull(7))
        {
            try
            {
                candidates = JsonSerializer.Deserialize<List<AddressCandidate>>(reader.GetString(7), Wire.JsonOptions);
            }
            catch (JsonException)
            {
                candidates = null;
            }
        }

        return new StoredDevice
        {
            DeviceId = reader.GetString(0),
            RoomId = reader.GetString(1),
            Name = reader.GetString(2),
            Platform = reader.IsDBNull(3) ? null : reader.GetString(3),
            PublicKey = reader.IsDBNull(4) ? null : reader.GetString(4),
            TcpPort = reader.IsDBNull(5) ? null : Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture),
            RemoteAddress = reader.IsDBNull(6) ? null : reader.GetString(6),
            Candidates = candidates,
            RegisteredAt = ParseTime(reader.GetString(8)),
            LastSeen = ParseTime(reader.GetString(9))
        };
    }

    private void Exec(string sql, Action<DbCommand>? bind = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        bind?.Invoke(cmd);
        cmd.ExecuteNonQuery();
    }

    private int ExecCount(string sql, Action<DbCommand>? bind = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        bind?.Invoke(cmd);
        return cmd.ExecuteNonQuery();
    }

    private string? ScalarString(string sql, Action<DbCommand>? bind = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        bind?.Invoke(cmd);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static void P(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    private static DateTimeOffset ParseTime(string raw)
        => DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
