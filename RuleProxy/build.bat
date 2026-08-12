@echo off
REM ============================================================
REM  RuleProxy 一键打包脚本（应用 exe + 安装程序 Setup）
REM  用法：双击 build.bat，或在命令行执行 build.bat
REM ============================================================
setlocal
cd /d "%~dp0"

echo [1/4] 检查 Python ...
py -3 -c "import sys; print(sys.version)" || goto :err

echo [2/4] 安装打包依赖 ...
py -3 -m pip install --upgrade pyinstaller pillow || goto :err

echo [3/4] 生成图标并打包应用 exe ...
py -3 tools\make_icon.py
py -3 -m PyInstaller --noconfirm --clean --onefile --windowed ^
    --name RuleProxy ^
    --icon icon.ico ^
    --add-data "icon.ico;." ^
    run.py || goto :err

echo [4/4] 打包安装程序 Setup ...
py -3 -m PyInstaller --noconfirm --clean --onefile --windowed ^
    --name "RuleProxy-Setup" ^
    --icon icon.ico ^
    --add-data "dist\RuleProxy.exe;." ^
    --add-data "icon.ico;." ^
    installer\installer_main.py || goto :err

echo.
echo ============================================================
echo  打包完成！
echo    应用 exe ： dist\RuleProxy.exe
echo    安装程序 ： dist\RuleProxy-Setup.exe
echo ============================================================
exit /b 0

:err
echo.
echo [错误] 打包失败，请检查上方输出。
exit /b 1
