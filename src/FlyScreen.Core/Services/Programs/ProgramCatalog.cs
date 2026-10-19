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
        RootDirectory = rootDirectory
            ?? Path.Combine(
                Path.GetDirectoryName(AppSettings.SettingsPath)!,
                "Programs");
        Directory.CreateDirectory(RootDirectory);
        Reload();
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

    public UserProgram Create(string name, ProgramLanguage language, string? nameSpace, string? fileName)
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
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        WriteScaffold(folder, prog);
        SaveMeta(prog);
        RefreshSize(prog);

        lock (_gate)
            _items.Add(prog);
        AppLog.Info($"已添加程序「{prog.Name}」({prog.LanguageText}) → {folder}");
        RaiseChanged();
        return prog;
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
        return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
    }

    public void WriteSource(UserProgram prog, string content)
    {
        var path = GetSourcePath(prog);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content ?? "", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        prog.UpdatedUtc = DateTime.UtcNow;
        SaveMeta(prog);
        RefreshSize(prog);
        RaiseChanged();
    }

    public void RefreshSize(UserProgram prog)
    {
        try
        {
            var path = GetSourcePath(prog);
            if (!File.Exists(path))
            {
                prog.SizeText = "—";
                return;
            }
            var len = new FileInfo(path).Length;
            prog.SizeText = FormatSize(len);
        }
        catch
        {
            prog.SizeText = "—";
        }
    }

    public void Delete(UserProgram prog)
    {
        var folder = GetFolder(prog);
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, $"删除程序目录失败「{prog.Name}」");
            throw;
        }

        lock (_gate)
            _items.Remove(prog);
        AppLog.Info($"已删除程序「{prog.Name}」");
        RaiseChanged();
    }

    public UserProgram? Find(string id)
    {
        lock (_gate)
            return _items.FirstOrDefault(p => p.Id == id);
    }

    private void WriteScaffold(string folder, UserProgram prog)
    {
        var sourcePath = Path.Combine(folder, prog.FileName);
        var body = BuildTemplate(prog);
        File.WriteAllText(sourcePath, body, new UTF8Encoding(false));

        if (prog.Language == ProgramLanguage.CSharp)
        {
            var csproj = Path.Combine(folder, "program.csproj");
            File.WriteAllText(csproj, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """, new UTF8Encoding(false));
        }
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
        ProgramLanguage.CSharp =>
            $$"""
            // namespace: {{prog.Namespace}}
            // name: {{prog.Name}}

            Console.WriteLine("Hello from {{prog.Name}}");
            """,
        ProgramLanguage.JavaScript =>
            $$"""
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
