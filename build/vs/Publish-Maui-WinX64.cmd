@echo off
REM 也可在资源管理器双击；VS 内请右键生成 FlyScreen.Publish.MauiWin 项目
cd /d "%~dp0..\.."
dotnet publish "src\FlyScreen.App.Maui\FlyScreen.App.Maui.csproj" -c Release ^
  -f net10.0-windows10.0.19041.0 ^
  -p:RuntimeIdentifierOverride=win-x64 ^
  -p:WindowsPackageType=None ^
  -p:WindowsAppSDKSelfContained=true ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -o "src\FlyScreen.App.Maui\bin\publish\maui-win-x64"
echo.
echo 产物: src\FlyScreen.App.Maui\bin\publish\maui-win-x64\FlyScreen.App.Maui.exe
pause
