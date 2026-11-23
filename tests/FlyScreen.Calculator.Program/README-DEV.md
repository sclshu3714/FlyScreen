# 外部开发（VS / VS Code）

语言：**C# (.NET)**（入口 `main.cs`）。

用 **Visual Studio** 或 **VS Code** 打开本文件夹即可编辑。
玄鸟内置编辑器适合小改；完整开发请用外部 IDE，再回玄鸟运行/调试。

## C#（DevHost）

- `main.cs` / `lib/*.cs`：脚本正文（与玄鸟一致，含 `ct` / `Delay` / `Fly`）。
- `DevHost.cs`：仅外部入口，**勿**加入 `program.json` → `ExtraFiles`。
- `Directory.Build.props`：`FlyScreenHostDir` / `FlyScreenCoreDll` 指向**已安装或调试中的玄鸟目录**下的 `FlyScreen.Core.dll`（不是源码 `.csproj`）。

若 props 路径不对，可设置环境变量 `FLYSCREEN_HOST_DIR`（玄鸟 exe 所在目录），或：

```bat
dotnet build -p:FlyScreenHostDir=C:\Path\To\FlyScreen
dotnet run --project program.csproj
```

`Ctrl+C` 取消 `ct`。设备类 `Fly.*` 请在手机玄鸟验证。

## VS Code

```bat
code .
```
