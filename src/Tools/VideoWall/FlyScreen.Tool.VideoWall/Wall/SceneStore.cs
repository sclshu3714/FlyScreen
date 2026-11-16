using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlyScreen.Tool.VideoWall.Contracts;

namespace FlyScreen.Tool.VideoWall.Wall;

/// <summary><c>video-wall.scenes.json</c> 场景 CRUD 与 AutoStart。</summary>
public sealed class SceneStore
{
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly List<WallScene> _scenes = [];

    public SceneStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("filePath 不能为空", nameof(filePath));

        FilePath = Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    public IReadOnlyList<WallScene> Scenes => _scenes;

    public WallScene? AutoStartScene => _scenes.FirstOrDefault(s => s.AutoStart);

    public async Task LoadAsync(CancellationToken ct = default)
    {
        _scenes.Clear();
        if (!File.Exists(FilePath)) return;

        var json = await File.ReadAllTextAsync(FilePath, ct).ConfigureAwait(false);
        var file = JsonSerializer.Deserialize<SceneFile>(json, JsonOptions);
        if (file?.Scenes is null) return;

        foreach (var s in file.Scenes)
            _scenes.Add(ToScene(s));
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var file = new SceneFile
        {
            SchemaVersion = SchemaVersion,
            Scenes = _scenes.Select(ToDto).ToList()
        };

        var json = JsonSerializer.Serialize(file, JsonOptions);
        var temp = FilePath + ".tmp";
        await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);
        File.Move(temp, FilePath, overwrite: true);
    }

    public void Upsert(WallScene scene)
    {
        var index = _scenes.FindIndex(s => s.Id == scene.Id);
        var updated = scene with { UpdatedAt = DateTimeOffset.Now };

        if (scene.AutoStart)
        {
            for (var i = 0; i < _scenes.Count; i++)
            {
                if (_scenes[i].AutoStart && _scenes[i].Id != scene.Id)
                    _scenes[i] = _scenes[i] with { AutoStart = false };
            }
        }

        if (index >= 0)
            _scenes[index] = updated;
        else
            _scenes.Add(updated);
    }

    public bool Remove(string id) => _scenes.RemoveAll(s => s.Id == id) > 0;

    public WallScene? Get(string id) => _scenes.FirstOrDefault(s => s.Id == id);

    public static string NewId() => $"scene-{Guid.NewGuid():N}"[..12];

    private static WallScene ToScene(SceneDto d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        Layout = new WallLayout { Rows = d.Rows, Columns = d.Columns },
        Tiles = d.Tiles?.Select(t => new TileBinding
        {
            Index = t.Index,
            SourceId = t.SourceId,
            TemporaryAddress = t.TemporaryAddress,
            Fit = t.Fit ?? "uniform",
            Muted = t.Muted
        }).ToList() ?? [],
        Audio = Enum.TryParse<AudioPolicy>(d.Audio, ignoreCase: true, out var audio) ? audio : AudioPolicy.Mute,
        SoloTile = d.SoloTile,
        CompositeShowLabels = d.CompositeShowLabels,
        AutoStart = d.AutoStart,
        UpdatedAt = d.UpdatedAt ?? DateTimeOffset.Now
    };

    private static SceneDto ToDto(WallScene s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Rows = s.Layout.Rows,
        Columns = s.Layout.Columns,
        Tiles = s.Tiles.Select(t => new TileDto
        {
            Index = t.Index,
            SourceId = t.SourceId,
            TemporaryAddress = t.TemporaryAddress,
            Fit = t.Fit,
            Muted = t.Muted
        }).ToList(),
        Audio = s.Audio.ToString(),
        SoloTile = s.SoloTile,
        CompositeShowLabels = s.CompositeShowLabels,
        AutoStart = s.AutoStart,
        UpdatedAt = s.UpdatedAt
    };

    private sealed class SceneFile
    {
        public int SchemaVersion { get; set; } = 1;
        public List<SceneDto> Scenes { get; set; } = [];
    }

    private sealed class SceneDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public int Rows { get; set; }
        public int Columns { get; set; }
        public List<TileDto>? Tiles { get; set; }
        public string? Audio { get; set; }
        public int? SoloTile { get; set; }
        public bool CompositeShowLabels { get; set; }
        public bool AutoStart { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
    }

    private sealed class TileDto
    {
        public int Index { get; set; }
        public string? SourceId { get; set; }
        public string? TemporaryAddress { get; set; }
        public string? Fit { get; set; }
        public bool Muted { get; set; }
    }
}