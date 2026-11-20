using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlyScreen.Models;

namespace FlyScreen.Services.Programs;

/// <summary>本机用户程序目录：创建 / 读写 / 生成脚手架。</summary>
public sealed class ProgramCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _gate = new();
    private readonly ObservableCollection<UserProgram> _items = [];

    public string RootDirectory { get; }

    public ObservableCollection<UserProgram> Items => _items;

    public event Action? Changed;

    public ProgramCatalog(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? global::FlyScreen.Services.FlyScreenDataPaths.ProgramsDirectory;
        Directory.CreateDirectory(RootDirectory);
        Reload();
        EnsureBuiltinPrograms();
    }

    public void Reload()
    {
        lock (_gate)
        {
            _items.Clear();
            if (!Directory.Exists(RootDirectory))
                Directory.CreateDirectory(RootDirectory);

            foreach (var dir in Directory.EnumerateDirectories(RootDirectory))
            {
                var meta = Path.Combine(dir, "program.json");
                if (!File.Exists(meta)) continue;
                try
                {
                    var json = File.ReadAllText(meta);
                    var prog = JsonSerializer.Deserialize<UserProgram>(json, JsonOptions);
                    if (prog == null || string.IsNullOrWhiteSpace(prog.Id)) continue;
                    // 目录名与 Id 对齐（迁移旧目录时容忍）
                    var folderId = Path.GetFileName(dir);
                    if (!string.Equals(folderId, prog.Id, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(folderId, SanitizeFolder(prog.Name), StringComparison.OrdinalIgnoreCase))
                    {
                        // keep as-is
                    }
                    if (string.Equals(prog.Id, BuiltinPrograms.GenericSwipeId, StringComparison.OrdinalIgnoreCase))
                        prog.IsBuiltin = true;
                    RefreshSize(prog);
                    _items.Add(prog);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"读取程序失败 {dir}: {ex.Message}");
                }
            }
        }
        RaiseChanged();
    }

    /// <summary>嵌入的通用识别脚本：写出/按版本升级源码，保留 settings.json。</summary>
    public void EnsureBuiltinPrograms()
    {
        try
        {
            EnsureGenericSwipeBuiltin();
        }
        catch (Exception ex)
        {
            AppLog.Warn("EnsureBuiltin: " + ex.Message);
        }
    }

    private void EnsureGenericSwipeBuiltin()
    {
        var id = BuiltinPrograms.GenericSwipeId;
        var folder = Path.Combine(RootDirectory, id);
        Directory.CreateDirectory(folder);

        UserProgram? existing;
        lock (_gate)
            existing = _items.FirstOrDefault(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

        var needWriteSource = true;
        if (existing != null
            && existing.BuiltinSourceVersion >= BuiltinPrograms.GenericSwipeSourceVersion
            && File.Exists(Path.Combine(folder, "main.cs")))
        {
            needWriteSource = false;
        }

        var embedded = ReadEmbeddedBuiltinSource();
        if (string.IsNullOrEmpty(embedded))
        {
            AppLog.Warn("内置通用脚本资源缺失");
            return;
        }

        if (needWriteSource)
        {
            var srcPath = Path.Combine(folder, "main.cs");
            File.WriteAllText(srcPath, NormalizeSourceText(embedded),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        EnsureBuiltinSchemaFile(folder, force: needWriteSource);

        var prog = existing ?? new UserProgram
        {
            Id = id,
            CreatedUtc = DateTime.UtcNow
        };
        prog.Name = BuiltinPrograms.GenericSwipeName;
        prog.Language = ProgramLanguage.CSharp;
        prog.Namespace = "com.xuanniao.builtin";
        prog.FileName = "main.cs";
        prog.IsBuiltin = true;
        prog.BuiltinSourceVersion = BuiltinPrograms.GenericSwipeSourceVersion;
        prog.UpdatedUtc = DateTime.UtcNow;
        SaveMeta(prog);
        RefreshSize(prog);

        lock (_gate)
        {
            if (existing == null)
                _items.Insert(0, prog);
            else
            {
                existing.Name = prog.Name;
                existing.IsBuiltin = true;
                existing.BuiltinSourceVersion = prog.BuiltinSourceVersion;
                existing.FileName = prog.FileName;
                existing.Language = prog.Language;
                existing.Namespace = prog.Namespace;
                RefreshSize(existing);
            }
        }
        RaiseChanged();
    }

    private static string ReadEmbeddedBuiltinSource()
        => ReadEmbeddedText(BuiltinPrograms.ResourceName, "generic_swipe_main.cs");

    private static void EnsureBuiltinSchemaFile(string folder, bool force)
    {
        var path = Path.Combine(folder, ProgramFlySettingsStore.SchemaFileName);
        if (!force && File.Exists(path) && ProgramFlySettingsStore.IsValidSchemaText(File.ReadAllText(path)))
            return;
        var json = ReadEmbeddedText(BuiltinPrograms.SchemaResourceName, "generic_swipe_fly_settings.json");
        if (string.IsNullOrWhiteSpace(json) || !ProgramFlySettingsStore.IsValidSchemaText(json))
            json = ProgramFlySettingsStore.EmptySchemaText;
        File.WriteAllText(path, ProgramFlySettingsStore.NormalizeSchemaText(json),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string ReadEmbeddedText(string logicalName, string fileNameSuffix)
    {
        var asm = typeof(ProgramCatalog).Assembly;
        using var stream = asm.GetManifestResourceStream(logicalName);
        if (stream == null)
        {
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(fileNameSuffix, StringComparison.OrdinalIgnoreCase));
            if (name == null) return "";
            using var s2 = asm.GetManifestResourceStream(name);
            if (s2 == null) return "";
            using var r2 = new StreamReader(s2, Encoding.UTF8);
            return r2.ReadToEnd();
        }
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public UserProgram Create(string name, ProgramLanguage language, string? nameSpace, string? fileName)
        => Create(name, language, nameSpace, fileName, ProgramUiKind.None);

    public UserProgram Create(
        string name,
        ProgramLanguage language,
        string? nameSpace,
        string? fileName,
        ProgramUiKind uiKind)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("程序名称不能为空");

        var ns = string.IsNullOrWhiteSpace(nameSpace)
            ? ChinesePinyin.ToNamespace(name)
            : nameSpace.Trim();
        if (!ns.StartsWith("com.xuanniao.", StringComparison.OrdinalIgnoreCase))
            ns = "com.xuanniao." + ChinesePinyin.ToSlug(ns);

        var file = string.IsNullOrWhiteSpace(fileName)
            ? ProgramLanguageInfo.DefaultFileName(language)
            : fileName.Trim();
        if (!file.EndsWith(ProgramLanguageInfo.Extension(language), StringComparison.OrdinalIgnoreCase))
            file = Path.GetFileNameWithoutExtension(file) + ProgramLanguageInfo.Extension(language);

        if (language != ProgramLanguage.CSharp)
            uiKind = ProgramUiKind.None;
        else if (uiKind != ProgramUiKind.None
                 && !ProgramUiCompatibility.CanRunLocal(uiKind))
        {
            throw new InvalidOperationException(
                $"本机宿主无法运行 {ProgramUiCompatibility.DisplayName(uiKind)} UI（HostKind={ProgramUiCompatibility.LocalHostKind}）。");
        }

        var id = Guid.NewGuid().ToString("N")[..12];
        var folder = Path.Combine(RootDirectory, id);
        Directory.CreateDirectory(folder);

        var prog = new UserProgram
        {
            Id = id,
            Name = name,
            Language = language,
            Namespace = ns,
            FileName = file,
            UiKind = uiKind,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        WriteScaffold(folder, prog);
        ProgramExternalDevScaffold.Ensure(folder, prog.Language, prog.Packages);
        ProgramUiScaffold.Apply(folder, prog);
        SaveMeta(prog);
        RefreshSize(prog);

        lock (_gate)
            _items.Add(prog);
        var uiNote = prog.UiKind == ProgramUiKind.None
            ? ""
            : $"，UI={ProgramUiCompatibility.DisplayName(prog.UiKind)}";
        AppLog.Info(
            $"已添加程序「{prog.Name}」({prog.LanguageText}{uiNote}) → {folder}（已生成外部 VS/VS Code 脚手架）",
            prog.Id, prog.Name);
        RaiseChanged();
        return prog;
    }

    /// <summary>为已有程序补齐外部开发文件（DevHost / README 等）。</summary>
    public void EnsureExternalDevScaffold(UserProgram prog)
    {
        ArgumentNullException.ThrowIfNull(prog);
        ProgramExternalDevScaffold.Ensure(GetFolder(prog), prog.Language, prog.Packages);
    }

    public void SaveMeta(UserProgram prog)
    {
        prog.UpdatedUtc = DateTime.UtcNow;
        var folder = GetFolder(prog);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "program.json");
        File.WriteAllText(path, JsonSerializer.Serialize(prog, JsonOptions), Encoding.UTF8);
    }

    public string GetFolder(UserProgram prog)
        => Path.Combine(RootDirectory, prog.Id);

    public string GetSourcePath(UserProgram prog)
        => Path.Combine(GetFolder(prog), prog.FileName);

    public string ReadSource(UserProgram prog)
    {
        var path = GetSourcePath(prog);
        if (!File.Exists(path)) return "";
        return NormalizeSourceText(File.ReadAllText(path, Encoding.UTF8));
    }

    public void WriteSource(UserProgram prog, string content)
        => WriteFile(prog, prog.FileName, content);

    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "program.json", "settings.json", "program.csproj", "packages.lock.json"
    };

    private static readonly HashSet<string> ReservedDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "obj", "bin", ".nuget"
    };

    /// <summary>入口 + ExtraFiles（相对路径，入口在前）。不含 fly-settings.json。</summary>
    public IReadOnlyList<string> ListProgramFiles(UserProgram prog)
    {
        NormalizeManifest(prog);
        var list = new List<string> { prog.FileName };
        foreach (var f in prog.ExtraFiles)
        {
            if (string.IsNullOrWhiteSpace(f)) continue;
            var rel = NormalizeRelativePath(f);
            if (string.Equals(rel, prog.FileName, StringComparison.OrdinalIgnoreCase)) continue;
            if (IsConfigSchemaFile(rel)) continue;
            if (!list.Contains(rel, StringComparer.OrdinalIgnoreCase))
                list.Add(rel);
        }
        return list;
    }

    /// <summary>编辑器文件切换列表：源文件 + 末尾 fly-settings.json（始终显示）。</summary>
    public IReadOnlyList<string> ListEditorFiles(UserProgram prog)
    {
        var list = ListProgramFiles(prog).ToList();
        if (!list.Contains(ProgramFlySettingsStore.SchemaFileName, StringComparer.OrdinalIgnoreCase))
            list.Add(ProgramFlySettingsStore.SchemaFileName);
        return list;
    }

    public static bool IsConfigSchemaFile(string? relativePath)
        => string.Equals(Path.GetFileName(relativePath ?? ""), ProgramFlySettingsStore.SchemaFileName,
            StringComparison.OrdinalIgnoreCase);

    public string ReadFile(UserProgram prog, string relativePath)
    {
        var rel = ResolveSafeRelative(prog, relativePath);
        var path = Path.Combine(GetFolder(prog), rel);
        if (IsConfigSchemaFile(rel))
            return ReadSchemaFile(prog, path);
        if (!File.Exists(path)) return "";
        return NormalizeSourceText(File.ReadAllText(path, Encoding.UTF8));
    }

    private string ReadSchemaFile(UserProgram prog, string path)
    {
        if (!File.Exists(path))
            return ProgramFlySettingsStore.EmptySchemaText;
        var text = NormalizeSourceText(File.ReadAllText(path, Encoding.UTF8));
        if (ProgramFlySettingsStore.IsValidSchemaText(text))
            return text;

        // 曾被源码误写入等污染：优先从旧注释抢救一次，否则写空模板
        try
        {
            var recovered = ProgramSettingsSchema.Parse(ReadSource(prog));
            if (recovered.Count > 0)
            {
                ProgramFlySettingsStore.SaveSchema(this, prog, recovered);
                AppLog.Info($"已从源码注释恢复 {ProgramFlySettingsStore.SchemaFileName}", prog.Id, prog.Name);
                return NormalizeSourceText(File.ReadAllText(path, Encoding.UTF8));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"抢救 {ProgramFlySettingsStore.SchemaFileName} 失败: {ex.Message}", prog.Id, prog.Name);
        }

        try
        {
            File.WriteAllText(path, ProgramFlySettingsStore.EmptySchemaText,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"修复 {ProgramFlySettingsStore.SchemaFileName} 失败: {ex.Message}");
        }
        return ProgramFlySettingsStore.EmptySchemaText;
    }

    public void WriteFile(UserProgram prog, string relativePath, string content)
    {
        var rel = ResolveSafeRelative(prog, relativePath);
        if (IsConfigSchemaFile(rel))
        {
            WriteSchemaFile(prog, rel, content);
            return;
        }
        EnsureWritable(prog);
        EnsureNotReservedMeta(rel);
        var path = Path.Combine(GetFolder(prog), rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = NormalizeSourceText(content);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        EnsureListed(prog, rel);
        prog.UpdatedUtc = DateTime.UtcNow;
        SaveMeta(prog);
        RefreshSize(prog);
        RaiseChanged();
    }

    /// <summary>fly-settings.json 允许内置程序写入；内容必须是合法 JSON。</summary>
    private void WriteSchemaFile(UserProgram prog, string rel, string content)
    {
        if (!ProgramFlySettingsStore.IsValidSchemaText(content))
            throw new InvalidOperationException(
                $"{ProgramFlySettingsStore.SchemaFileName} 必须是合法 JSON，不能写入源码内容");
        var path = Path.Combine(GetFolder(prog), rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = ProgramFlySettingsStore.NormalizeSchemaText(content);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        prog.UpdatedUtc = DateTime.UtcNow;
        SaveMeta(prog);
        RefreshSize(prog);
        RaiseChanged();
    }

    /// <summary>新增附加源文件（默认内容为空模板）；返回规范化相对路径。</summary>
    public string AddFile(UserProgram prog, string relativePath, string? initialContent = null)
    {
        EnsureWritable(prog);
        var rel = ResolveSafeRelative(prog, relativePath);
        if (string.Equals(rel, prog.FileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("不能添加与入口同名的附加文件");
        if (IsConfigSchemaFile(rel))
            throw new InvalidOperationException("fly-settings.json 已在文件列表中，请直接打开编辑");
        EnsureNotReservedMeta(rel);
        var path = Path.Combine(GetFolder(prog), rel);
        if (File.Exists(path))
            throw new InvalidOperationException($"文件已存在：{rel}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = NormalizeSourceText(initialContent ?? BuildExtraFileStub(prog, rel));
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        EnsureListed(prog, rel);
        prog.UpdatedUtc = DateTime.UtcNow;
        SaveMeta(prog);
        RefreshSize(prog);
        RaiseChanged();
        return rel;
    }

    public void RemoveFile(UserProgram prog, string relativePath)
    {
        EnsureWritable(prog);
        var rel = ResolveSafeRelative(prog, relativePath);
        if (string.Equals(rel, prog.FileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("不能删除入口文件");
        EnsureNotReservedMeta(rel);
        var path = Path.Combine(GetFolder(prog), rel);
        if (File.Exists(path))
            File.Delete(path);
        prog.ExtraFiles.RemoveAll(f =>
            string.Equals(NormalizeRelativePath(f), rel, StringComparison.OrdinalIgnoreCase));
        prog.UpdatedUtc = DateTime.UtcNow;
        SaveMeta(prog);
        RefreshSize(prog);
        RaiseChanged();
    }

    public string RenameFile(UserProgram prog, string relativePath, string newRelativePath)
    {
        EnsureWritable(prog);
        var from = ResolveSafeRelative(prog, relativePath);
        var to = ResolveSafeRelative(prog, newRelativePath);
        if (string.Equals(from, prog.FileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请通过元数据修改入口文件名");
        EnsureNotReservedMeta(from);
        EnsureNotReservedMeta(to);
        var folder = GetFolder(prog);
        var fromPath = Path.Combine(folder, from);
        var toPath = Path.Combine(folder, to);
        if (!File.Exists(fromPath))
            throw new FileNotFoundException("文件不存在", from);
        if (File.Exists(toPath))
            throw new InvalidOperationException($"目标已存在：{to}");
        Directory.CreateDirectory(Path.GetDirectoryName(toPath)!);
        File.Move(fromPath, toPath);
        for (var i = 0; i < prog.ExtraFiles.Count; i++)
        {
            if (string.Equals(NormalizeRelativePath(prog.ExtraFiles[i]), from, StringComparison.OrdinalIgnoreCase))
                prog.ExtraFiles[i] = to;
        }
        EnsureListed(prog, to);
        prog.UpdatedUtc = DateTime.UtcNow;
        SaveMeta(prog);
        RefreshSize(prog);
        RaiseChanged();
        return to;
    }

    /// <summary>复制本地 DLL 到 refs/ 并登记 LocalDlls；返回相对路径。</summary>
    public string AddLocalDll(UserProgram prog, string sourceDllPath)
    {
        EnsureWritable(prog);
        if (string.IsNullOrWhiteSpace(sourceDllPath) || !File.Exists(sourceDllPath))
            throw new FileNotFoundException("DLL 不存在", sourceDllPath);
        var name = Path.GetFileName(sourceDllPath);
        if (string.IsNullOrEmpty(name) || !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("仅支持 .dll 文件");
        if (!ProgramScriptHost.HasManagedMetadata(sourceDllPath))
            throw new BadImageFormatException(
                $"「{name}」不是托管程序集（无 CLR 元数据，可能是原生/WinRT DLL），不能作为脚本编译引用。",
                sourceDllPath);
        var rel = NormalizeRelativePath(Path.Combine("refs", name));
        var dest = Path.Combine(GetFolder(prog), rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(sourceDllPath, dest, overwrite: true);
        prog.LocalDlls ??= [];
        if (!prog.LocalDlls.Any(d => string.Equals(NormalizeRelativePath(d), rel, StringComparison.OrdinalIgnoreCase)))
            prog.LocalDlls.Add(rel);
        prog.UpdatedUtc = DateTime.UtcNow;
        SaveMeta(prog);
        RaiseChanged();
        return rel;
    }

    public void RemoveLocalDll(UserProgram prog, string relativePath)
    {
        EnsureWritable(prog);
        var rel = ResolveSafeRelative(prog, relativePath);
        var path = Path.Combine(GetFolder(prog), rel);
        if (File.Exists(path))
            File.Delete(path);
        prog.LocalDlls?.RemoveAll(d =>
            string.Equals(NormalizeRelativePath(d), rel, StringComparison.OrdinalIgnoreCase));
        prog.UpdatedUtc = DateTime.UtcNow;
        SaveMeta(prog);
        RaiseChanged();
    }

    /// <summary>读取入口 + ExtraFiles 中的 <c>.cs</c>，供 Roslyn 编译/运行（跳过 xaml 等资源）。</summary>
    public IReadOnlyList<(string RelativePath, string Content)> ReadExtraSources(UserProgram prog)
    {
        NormalizeManifest(prog);
        var list = new List<(string, string)>();
        foreach (var f in prog.ExtraFiles)
        {
            if (string.IsNullOrWhiteSpace(f)) continue;
            var rel = NormalizeRelativePath(f);
            if (string.Equals(rel, prog.FileName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!rel.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add((rel, ReadFile(prog, rel)));
        }
        return list;
    }

    /// <summary>UI 附属文件（xaml/axaml 等），供共享；不参与 Roslyn。</summary>
    public IReadOnlyList<(string RelativePath, string Content)> ReadUiAssetSources(UserProgram prog)
    {
        NormalizeManifest(prog);
        if (prog.UiKind == ProgramUiKind.None) return [];
        var folder = GetFolder(prog);
        var names = prog.UiKind switch
        {
            ProgramUiKind.Wpf => new[] { "MainWindow.xaml", "MainWindow.xaml.cs" },
            ProgramUiKind.Avalonia => new[] { "MainWindow.axaml", "MainWindow.axaml.cs" },
            ProgramUiKind.Maui => new[] { "ProgramUiPage.xaml", "ProgramUiPage.xaml.cs" },
            _ => Array.Empty<string>()
        };
        var list = new List<(string, string)>();
        foreach (var name in names)
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path)) continue;
            list.Add((NormalizeRelativePath(name), NormalizeSourceText(File.ReadAllText(path, Encoding.UTF8))));
        }
        return list;
    }

    /// <summary>
    /// 解析编译用本地 DLL 绝对路径：program.json LocalDlls + 程序目录 refs/*.dll。
    /// 与 DevHost 一致；refs 落盘即可引用，避免仅依赖清单时被编辑器保存冲掉 LocalDlls。
    /// 调用方须经 <see cref="ProgramScriptHost.TryCreateReferenceFromFile"/>：无 CLR 元数据的原生 PE 会跳过。
    /// </summary>
    public IReadOnlyList<string> ResolveLocalDllPaths(UserProgram prog)
    {
        NormalizeManifest(prog);
        var folder = GetFolder(prog);
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            try
            {
                var full = Path.GetFullPath(path);
                if (!seen.Add(full)) return;
                list.Add(full);
            }
            catch
            {
                // skip invalid
            }
        }

        foreach (var d in prog.LocalDlls)
        {
            if (string.IsNullOrWhiteSpace(d)) continue;
            try
            {
                var rel = ResolveSafeRelative(prog, d);
                TryAdd(Path.Combine(folder, rel));
            }
            catch
            {
                // skip invalid
            }
        }

        var refsDir = Path.Combine(folder, "refs");
        if (Directory.Exists(refsDir))
        {
            try
            {
                foreach (var dll in Directory.EnumerateFiles(refsDir, "*.dll"))
                    TryAdd(dll);
            }
            catch
            {
                // skip unreadable refs/
            }
        }

        return list;
    }

    public void NormalizeManifest(UserProgram prog)
    {
        prog.ExtraFiles ??= [];
        prog.LocalDlls ??= [];
        prog.Packages ??= [];
        prog.ExtraFiles = prog.ExtraFiles
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(NormalizeRelativePath)
            .Where(f => !string.Equals(f, prog.FileName, StringComparison.OrdinalIgnoreCase))
            .Where(f => !IsConfigSchemaFile(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        prog.LocalDlls = prog.LocalDlls
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(NormalizeRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        prog.Packages = prog.Packages
            .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Id))
            .GroupBy(p => p.Id.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();
    }

    private void EnsureListed(UserProgram prog, string relativePath)
    {
        NormalizeManifest(prog);
        if (string.Equals(relativePath, prog.FileName, StringComparison.OrdinalIgnoreCase))
            return;
        if (IsConfigSchemaFile(relativePath))
            return;
        if (!prog.ExtraFiles.Any(f => string.Equals(f, relativePath, StringComparison.OrdinalIgnoreCase)))
            prog.ExtraFiles.Add(relativePath);
    }

    private static void EnsureWritable(UserProgram prog)
    {
        if (prog.IsBuiltin
            || string.Equals(prog.Id, BuiltinPrograms.GenericSwipeId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("内置程序源码为只读，请用「设置」修改参数");
    }

    private static void EnsureNotReservedMeta(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        if (ReservedFileNames.Contains(name))
            throw new InvalidOperationException($"不能把「{name}」当作源文件编辑");
        var first = relativePath.Split('/', '\\')[0];
        if (ReservedDirNames.Contains(first))
            throw new InvalidOperationException($"不能写入保留目录「{first}」");
    }

    /// <summary>规范化相对路径并确保落在程序根内。</summary>
    public string ResolveSafeRelative(UserProgram prog, string relativePath)
    {
        var rel = NormalizeRelativePath(relativePath);
        if (string.IsNullOrEmpty(rel) || rel is "." or "..")
            throw new ArgumentException("无效路径");
        var root = Path.GetFullPath(GetFolder(prog));
        if (!root.EndsWith(Path.DirectorySeparatorChar) && !root.EndsWith(Path.AltDirectorySeparatorChar))
            root += Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(GetFolder(prog), rel));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("路径越出程序目录");
        // 返回相对根的正斜杠形式，便于跨端
        var relative = Path.GetRelativePath(GetFolder(prog), full);
        return NormalizeRelativePath(relative);
    }

    public static string NormalizeRelativePath(string path)
    {
        var s = (path ?? "").Trim().Replace('\\', '/');
        while (s.StartsWith("./", StringComparison.Ordinal))
            s = s[2..];
        s = s.TrimStart('/');
        if (s.Contains("..", StringComparison.Ordinal))
        {
            // 先按段过滤 ..
            var parts = s.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var stack = new List<string>();
            foreach (var p in parts)
            {
                if (p == ".") continue;
                if (p == "..")
                {
                    if (stack.Count == 0)
                        throw new InvalidOperationException("路径越出程序目录");
                    stack.RemoveAt(stack.Count - 1);
                    continue;
                }
                stack.Add(p);
            }
            s = string.Join('/', stack);
        }
        return s;
    }

    private static string BuildExtraFileStub(UserProgram prog, string relativePath)
    {
        var name = Path.GetFileNameWithoutExtension(relativePath);
        var ns = SanitizeCsNamespace(prog.Namespace);
        var ident = SanitizeCsIdent(name);
        return prog.Language switch
        {
            ProgramLanguage.CSharp =>
                "// " + relativePath + "\n"
                + "namespace " + ns + "\n{\n"
                + "    public static class " + ident + "\n    {\n    }\n}\n",
            ProgramLanguage.JavaScript =>
                "// " + relativePath + "\n"
                + "export function hello() {\n  return \"" + name + "\";\n}\n",
            ProgramLanguage.Python =>
                "# " + relativePath + "\n\ndef hello():\n    return \"" + name + "\"\n",
            ProgramLanguage.PowerShell =>
                "# " + relativePath + "\nfunction Get-Hello { \"" + name + "\" }\n",
            ProgramLanguage.Bash =>
                "# " + relativePath + "\nhello() { echo \"" + name + "\"; }\n",
            ProgramLanguage.Batch =>
                "@echo off\nrem " + relativePath + "\n",
            _ => "# " + relativePath + "\n"
        };
    }

    private static string SanitizeCsNamespace(string? ns)
    {
        var s = string.IsNullOrWhiteSpace(ns) ? "FlyUser" : ns.Trim();
        var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizeCsIdent)
            .Where(p => p.Length > 0);
        var joined = string.Join('.', parts);
        return string.IsNullOrEmpty(joined) ? "FlyUser" : joined;
    }

    private static string SanitizeCsIdent(string? name)
    {
        var s = Regex.Replace(name ?? "Lib", @"[^\w]", "_");
        if (s.Length == 0 || char.IsDigit(s[0]))
            s = "L" + s;
        return s;
    }

    /// <summary>
    /// 修复因 WebView EvaluateJavaScript 往返把换行编成字面量 "\r\n" 的源码。
    /// </summary>
    public static string NormalizeSourceText(string? content)
    {
        if (string.IsNullOrEmpty(content)) return "";
        var s = content;
        // 无真实换行、却含字面量 \r\n / \n → 整段按 JSON 转义还原
        if (!s.Contains('\n') && !s.Contains('\r')
            && (s.Contains("\\r\\n", StringComparison.Ordinal) || s.Contains("\\n", StringComparison.Ordinal)))
        {
            s = s
                .Replace("\\r\\n", "\n", StringComparison.Ordinal)
                .Replace("\\n", "\n", StringComparison.Ordinal)
                .Replace("\\r", "\n", StringComparison.Ordinal)
                .Replace("\\t", "\t", StringComparison.Ordinal)
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }
        else if (s.Contains("\\\"", StringComparison.Ordinal) && s.Contains('$'))
        {
            // 换行已修好但仍残留 JSON 引号转义（如 $" → $\"）
            s = s.Replace("\\\"", "\"", StringComparison.Ordinal);
        }
        return s.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    public void RefreshSize(UserProgram prog)
    {
        try
        {
            NormalizeManifest(prog);
            long len = 0;
            foreach (var rel in ListProgramFiles(prog))
            {
                var path = Path.Combine(GetFolder(prog), ResolveSafeRelative(prog, rel));
                if (File.Exists(path))
                    len += new FileInfo(path).Length;
            }
            prog.SizeText = len <= 0 ? "—" : FormatSize(len);
        }
        catch
        {
            prog.SizeText = "—";
        }
    }

    public void Delete(UserProgram prog)
    {
        if (prog.IsBuiltin
            || string.Equals(prog.Id, BuiltinPrograms.GenericSwipeId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"「{prog.Name}」为内置程序，不可删除");

        var folder = GetFolder(prog);
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, $"删除程序目录失败「{prog.Name}」", prog.Id, prog.Name);
            throw;
        }

        lock (_gate)
            _items.Remove(prog);
        AppLog.Info($"已删除程序「{prog.Name}」", prog.Id, prog.Name);
        RaiseChanged();
    }

    public UserProgram? Find(string id)
    {
        lock (_gate)
            return _items.FirstOrDefault(p => p.Id == id);
    }

    public UserProgram? FindDuplicate(string? id, string? name)
    {
        var trimmed = (name ?? "").Trim();
        lock (_gate)
        {
            return _items.FirstOrDefault(p =>
                (!string.IsNullOrEmpty(id) && string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(trimmed) && string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase)));
        }
    }

    /// <summary>写入对端共享来的程序。同 Id 或同名已存在时不覆盖。</summary>
    public bool TryImportShared(
        UserProgram incoming,
        string? source,
        string? projectFile,
        IReadOnlyList<ProgramShareFile>? extraSources = null)
    {
        incoming.Name = (incoming.Name ?? "").Trim();
        if (string.IsNullOrEmpty(incoming.Name)) return false;
        if (!IsSafeId(incoming.Id)) return false;
        if (FindDuplicate(incoming.Id, incoming.Name) != null) return false;

        var folder = GetFolder(incoming);
        if (Directory.Exists(folder)) return false;

        incoming.FileName = SanitizeSharedFileName(incoming);
        if (string.IsNullOrWhiteSpace(incoming.Namespace))
            incoming.Namespace = "com.xuanniao.app";
        incoming.Breakpoints ??= [];
        incoming.ExtraFiles ??= [];
        incoming.LocalDlls ??= [];
        incoming.Packages ??= [];
        incoming.CreatedUtc = DateTime.UtcNow;
        incoming.UpdatedUtc = DateTime.UtcNow;

        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "lib"));
        File.WriteAllText(
            Path.Combine(folder, incoming.FileName),
            source ?? "",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (incoming.Language == ProgramLanguage.CSharp && !string.IsNullOrWhiteSpace(projectFile))
        {
            File.WriteAllText(
                Path.Combine(folder, "program.csproj"),
                projectFile,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        ApplyExtraSources(incoming, extraSources);
        ProgramExternalDevScaffold.Ensure(folder, incoming.Language, incoming.Packages);
        ProgramUiScaffold.Apply(folder, incoming);

        SaveMeta(incoming);
        RefreshSize(incoming);
        lock (_gate)
            _items.Add(incoming);
        AppLog.Info($"已接收共享程序「{incoming.Name}」", incoming.Id, incoming.Name);
        RaiseChanged();
        return true;
    }

    /// <summary>覆盖已有程序的附加源（同 Id 共享更新）。</summary>
    public void ApplyExtraSources(UserProgram prog, IReadOnlyList<ProgramShareFile>? extraSources)
    {
        NormalizeManifest(prog);
        var folder = GetFolder(prog);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (extraSources != null)
        {
            foreach (var item in extraSources)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Path)) continue;
                string rel;
                try { rel = ResolveSafeRelative(prog, item.Path); }
                catch { continue; }
                if (string.Equals(rel, prog.FileName, StringComparison.OrdinalIgnoreCase)) continue;
                try { EnsureNotReservedMeta(rel); }
                catch { continue; }
                var path = Path.Combine(folder, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, item.Content ?? "", new UTF8Encoding(false));
                keep.Add(rel);
            }
        }

        // 删除清单中已不在共享载荷里的附加文件
        foreach (var old in prog.ExtraFiles.ToList())
        {
            var rel = NormalizeRelativePath(old);
            if (keep.Contains(rel)) continue;
            try
            {
                var path = Path.Combine(folder, ResolveSafeRelative(prog, rel));
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* ignore */ }
        }
        prog.ExtraFiles = keep.ToList();
    }

    private static bool IsSafeId(string? id)
        => !string.IsNullOrEmpty(id)
           && id.Length is >= 8 and <= 32
           && id.All(c => char.IsAsciiHexDigit(c));

    private static string SanitizeSharedFileName(UserProgram prog)
    {
        var file = Path.GetFileName((prog.FileName ?? "").Trim());
        var ext = ProgramLanguageInfo.Extension(prog.Language);
        if (string.IsNullOrEmpty(file) || file is "." or "..")
            return ProgramLanguageInfo.DefaultFileName(prog.Language);
        if (!file.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            file = Path.GetFileNameWithoutExtension(file) + ext;
        return file;
    }

    private void WriteScaffold(string folder, UserProgram prog)
    {
        var sourcePath = Path.Combine(folder, prog.FileName);
        var body = BuildTemplate(prog);
        File.WriteAllText(sourcePath, body, new UTF8Encoding(false));
        Directory.CreateDirectory(Path.Combine(folder, "lib"));
        if (prog.Language == ProgramLanguage.CSharp)
            Directory.CreateDirectory(Path.Combine(folder, "refs"));
        // DevHost / README / props 由 ProgramExternalDevScaffold.Ensure 统一写出
    }

    private static string BuildTemplate(UserProgram prog) => prog.Language switch
    {
        ProgramLanguage.Python =>
            $"""
            # -*- coding: utf-8 -*-
            # package: {prog.Namespace}
            # name: {prog.Name}

            def main():
                print("Hello from {prog.Name}")


            if __name__ == "__main__":
                main()
            """,
        ProgramLanguage.CSharp => InProcessProgramExecutor.IsMobileHost
            ? $$"""
            // namespace: {{prog.Namespace}}
            // name: {{prog.Name}}
            // 手机脚本：Fly 读界面 / OCR / 点击 / 滑动（需开启「玄鸟键鼠注入」无障碍；Ocr 需 Android 11+）
            // 红果免费漫剧包名多为 com.kylin.read；广告结束出现「上滑继续观看…」时自动上滑

            Console.WriteLine("ready=" + Fly.Ready + " app=" + Fly.App);
            if (!Fly.Ready)
            {
                Console.WriteLine("请开启无障碍：玄鸟键鼠注入");
                Fly.OpenSettings();
                return;
            }

            // v4.1：仅「上滑/继续观看剧集」；禁左滑；横屏单次大区域 OCR（避免截屏限流）
            Console.WriteLine("=== 短剧上滑模板 v4.1 ===");
            string[] apps = { "com.kylin.read", "kylin", "hongguo" };
            string[] tips = { "上滑继续观看漫剧", "上滑继续观看短剧", "上滑继续观看剧集", "上滑继续观看", "上滑继续", "继续观看剧集" };
            var tipArmed = true;
            long coolUntil = 0;
            while (!ct.IsCancellationRequested)
            {
                var app = Fly.App ?? "";
                if (app.Length > 0 && !apps.Any(h => app.Contains(h, StringComparison.OrdinalIgnoreCase)))
                {
                    tipArmed = true;
                    await Delay(1200);
                    continue;
                }

                var hit = tips.FirstOrDefault(t => Fly.HasText(t));
                var w = Fly.ScreenWidth;
                var h = Fly.ScreenHeight;
                var landscape = w > h && h > 0;
                if (hit == null && w > 0 && h > 0)
                {
                    var l = landscape ? (int)(w * 0.28) : (int)(w * 0.10);
                    var t0 = landscape ? (int)(h * 0.00) : (int)(h * 0.32);
                    var r = landscape ? (int)(w * 0.99) : (int)(w * 0.90);
                    var b = landscape ? (int)(h * 0.32) : (int)(h * 0.62);
                    var ocr = (Fly.Ocr(l, t0, r, b) ?? "").Trim();
                    Console.WriteLine("OCR len=" + ocr.Length + " [" + (ocr.Length <= 60 ? ocr : ocr.Substring(0, 60)) + "]");
                    hit = tips.FirstOrDefault(k => ocr.Contains(k, StringComparison.OrdinalIgnoreCase));
                    if (hit == null && ocr.Contains("上滑", StringComparison.Ordinal) && ocr.Contains("继续", StringComparison.Ordinal))
                        hit = "上滑继续";
                }
                if (hit == null)
                {
                    tipArmed = true;
                    await Delay(landscape ? 900 : 700);
                    continue;
                }

                var now = Environment.TickCount64;
                if (!tipArmed || now < coolUntil)
                {
                    await Delay(400);
                    continue;
                }
                tipArmed = false;
                coolUntil = now + 10000;
                var adCta = Fly.HasText("查看详情");
                var cx = adCta ? (landscape ? 0.22 : 0.28) : (landscape ? 0.30 : 0.34);
                var y0 = adCta ? (landscape ? 0.62 : 0.60) : (landscape ? 0.70 : 0.68);
                var y1 = landscape ? 0.12 : 0.20;
                Console.WriteLine("已识别「" + hit + "」→ 安全上滑 " + (landscape ? "横屏" : "竖屏") + " " + w + "x" + h);
                var ok = Fly.Swipe(cx, y0, cx, y1, landscape ? 580 : 440);
                if (!ok)
                {
                    Console.WriteLine("上滑失败：手势取消");
                    tipArmed = true;
                    coolUntil = now + 2500;
                    await Delay(500);
                    continue;
                }
                for (var i = 0; i < 8 && !ct.IsCancellationRequested; i++)
                {
                    await Delay(400);
                    if (!Fly.HasText(hit)) break;
                }
                tipArmed = true;
            }
            """
            : $$"""
            // namespace: {{prog.Namespace}}
            // name: {{prog.Name}}

            Console.WriteLine("Hello from {{prog.Name}}");
            """,
        ProgramLanguage.JavaScript => InProcessProgramExecutor.IsMobileHost
            ? $$"""
            // package: {{prog.Namespace}}
            // name: {{prog.Name}}
            // 手机脚本：Fly.hasText / Fly.swipeUp / sleep(ms)

            console.log("ready=" + Fly.ready() + " app=" + Fly.app());
            if (!Fly.ready()) {
              console.warn("请开启无障碍：玄鸟键鼠注入");
              Fly.openSettings();
            } else {
              while (true) {
                if (Fly.hasText("上滑继续观看短剧")) {
                  console.log("检测到上滑提示 → swipeUp");
                  Fly.swipeUp();
                  sleep(1500);
                } else {
                  sleep(800);
                }
              }
            }
            """
            : $$"""
            // package: {{prog.Namespace}}
            // name: {{prog.Name}}

            console.log("Hello from {{prog.Name}}");
            """,
        ProgramLanguage.PowerShell =>
            $$"""
            # package: {{prog.Namespace}}
            # name: {{prog.Name}}

            Write-Host "Hello from {{prog.Name}}"
            """,
        ProgramLanguage.Bash =>
            $$"""
            #!/usr/bin/env bash
            # package: {{prog.Namespace}}
            # name: {{prog.Name}}

            echo "Hello from {{prog.Name}}"
            """,
        ProgramLanguage.Batch =>
            $$"""
            @echo off
            rem package: {{prog.Namespace}}
            rem name: {{prog.Name}}

            echo Hello from {{prog.Name}}
            """,
        _ => $"# {prog.Name}\n"
    };

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / (1024.0 * 1024):0.##} MB";
    }

    private static string SanitizeFolder(string name)
    {
        var s = Regex.Replace(name ?? "", @"[^\w\-\u4e00-\u9fff]+", "_");
        return string.IsNullOrWhiteSpace(s) ? "app" : s.Trim('_');
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { /* ignore */ }
    }
}
