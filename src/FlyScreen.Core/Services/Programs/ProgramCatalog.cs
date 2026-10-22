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
        AppLog.Info($"已添加程序「{prog.Name}」({prog.LanguageText}) → {folder}", prog.Id, prog.Name);
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
        if (!File.Exists(path)) return "";
        return NormalizeSourceText(File.ReadAllText(path, Encoding.UTF8));
    }

    public void WriteSource(UserProgram prog, string content)
    {
        var path = GetSourcePath(prog);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = NormalizeSourceText(content);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        prog.UpdatedUtc = DateTime.UtcNow;
        SaveMeta(prog);
        RefreshSize(prog);
        RaiseChanged();
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
    public bool TryImportShared(UserProgram incoming, string? source, string? projectFile)
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
        incoming.CreatedUtc = DateTime.UtcNow;
        incoming.UpdatedUtc = DateTime.UtcNow;

        Directory.CreateDirectory(folder);
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

        SaveMeta(incoming);
        RefreshSize(incoming);
        lock (_gate)
            _items.Add(incoming);
        AppLog.Info($"已接收共享程序「{incoming.Name}」", incoming.Id, incoming.Name);
        RaiseChanged();
        return true;
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

            string[] apps = { "com.kylin.read", "kylin", "hongguo" };
            string[] tips = { "上滑继续观看漫剧", "上滑继续观看短剧", "上滑继续观看", "继续观看漫剧" };
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
                if (hit == null)
                {
                    tipArmed = true;
                    await Delay(700);
                    continue;
                }
                var now = Environment.TickCount64;
                if (!tipArmed || now < coolUntil)
                {
                    await Delay(400);
                    continue;
                }
                tipArmed = false;
                coolUntil = now + 8000;
                var w = Fly.ScreenWidth;
                var h = Fly.ScreenHeight;
                var landscape = w > h && h > 0;
                var dist = landscape ? 0.62 : 0.50;
                var dur = landscape ? 450 : 380;
                Console.WriteLine("已识别「" + hit + "」→ SwipeUp " + (landscape ? "横屏" : "竖屏") + " " + w + "x" + h);
                var ok = Fly.SwipeUp(dist, dur);
                if (!ok)
                {
                    Console.WriteLine("上滑失败：手势取消（识别成功）");
                    tipArmed = true;
                    coolUntil = now + 1500;
                    await Delay(600);
                    continue;
                }
                var cleared = false;
                for (var i = 0; i < 10 && !ct.IsCancellationRequested; i++)
                {
                    await Delay(400);
                    if (!Fly.HasText(hit)) { cleared = true; break; }
                }
                if (!cleared)
                {
                    Console.WriteLine("上滑完成但提示仍在 → 左滑补救");
                    Fly.Swipe(0.78, 0.50, 0.22, 0.50, 420);
                    await Delay(800);
                }
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
