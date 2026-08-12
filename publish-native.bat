@echo off
REM ============================================================
REM RuleProxy 原生版（C# / .NET 8 WPF）发布脚本
REM 产物：dist\native\RuleProxy.exe（单文件、自包含、带图标）
REM ============================================================
setlocal
cd /d "%~dp0"

set DOTNET="C:\Program Files\dotnet\dotnet.exe"

echo [1/3] 还原并构建...
%DOTNET% build "RuleProxy.Native\RuleProxy.Native.csproj" -c Release -v q || goto :err

echo [2/3] 发布为单文件自包含 exe...
%DOTNET% publish "RuleProxy.Native\RuleProxy.Native.csproj" -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -o "dist\native" -v q || goto :err

echo [3/3] 复制测试项目（可选）...
echo.
echo 完成！产物：dist\native\RuleProxy.exe
echo 直接双击运行；也支持命令行参数 --minimized 启动即最小化到托盘。
goto :eof

:err
echo 构建失败，请检查上方错误信息。
exit /b 1
