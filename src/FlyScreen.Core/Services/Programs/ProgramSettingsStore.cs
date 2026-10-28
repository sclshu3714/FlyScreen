using System.Text.Json;

namespace FlyScreen.Services.Programs;

/// <summary>程序目录下 settings.json：覆盖脚本 @fly-settings 默认值。</summary>
public static class ProgramSettingsStore
{
    public const string FileName = "settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string PathFor(ProgramCatalog catalog, UserProgram prog)
        => System.IO.Path.Combine(catalog.GetFolder(prog), FileName);

    public static ProgramSettingsFile LoadFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new ProgramSettingsFile();
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<ProgramSettingsFile>(json, JsonOptions);
            if (file == null) return new ProgramSettingsFile();
            file.Values ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return file;
        }
        catch
        {
            return new ProgramSettingsFile();
        }
    }

    public static void SaveFile(string path, ProgramSettingsFile file)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        file.Values ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var json = JsonSerializer.Serialize(file, JsonOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>合并：磁盘覆盖 &gt; 标注默认。</summary>
    public static Dictionary<string, string> Resolve(
        IReadOnlyList<ProgramSettingField> schema,
        ProgramSettingsFile? disk)
    {
        var map = ProgramSettingsSchema.DefaultsMap(schema);
        if (disk?.Values == null) return map;
        foreach (var (k, v) in disk.Values)
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            var field = schema.FirstOrDefault(f =>
                string.Equals(f.Key, k, StringComparison.OrdinalIgnoreCase));
            map[k] = field != null
                ? ProgramSettingsSchema.NormalizeStored(field.Kind, v)
                : (v ?? "");
        }
        ProgramRegionKeys.MigrateLegacy(map);
        return map;
    }

    public static Dictionary<string, string> LoadResolved(ProgramCatalog catalog, UserProgram prog)
    {
        var source = catalog.ReadSource(prog);
        var schema = ProgramSettingsSchema.Parse(source);
        var disk = LoadFile(PathFor(catalog, prog));
        return Resolve(schema, disk);
    }

    public static void SaveValues(ProgramCatalog catalog, UserProgram prog, IReadOnlyDictionary<string, string> values)
    {
        var path = PathFor(catalog, prog);
        var file = LoadFile(path);
        file.Values = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        SaveFile(path, file);
        prog.UpdatedUtc = DateTime.UtcNow;
        catalog.SaveMeta(prog);
    }
}
