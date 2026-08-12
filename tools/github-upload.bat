@echo off
REM ============================================================
REM  RuleProxy 一键上传到 GitHub（推送到已建好的仓库）
REM  仓库： https://github.com/devjustin625/ruleproxy
REM  前置：已安装 git 与 gh 并已 gh auth login（浏览器登录）
REM  安装：winget install --id Git.Git -e --source winget
REM        winget install --id GitHub.cli -e --source winget
REM        gh auth login
REM ============================================================
setlocal
cd /d "%~dp0.."
set REPO=devjustin625/ruleproxy

echo ============================================================
echo  RuleProxy 上传到 GitHub（仓库: %REPO%）
echo ============================================================

git --version >nul 2>&1 || (echo [错误] 未安装 Git，请先运行: winget install --id Git.Git -e --source winget & exit /b 1)
gh --version  >nul 2>&1 || (echo [错误] 未安装 GitHub CLI，请先运行: winget install --id GitHub.cli -e --source winget & exit /b 1)
gh auth status >nul 2>&1 || (echo [错误] 未登录 GitHub，请先运行: gh auth login & exit /b 1)

echo [1/4] 初始化仓库并提交源码 ...
if not exist .git git init -b main
git add -A
git -c user.name="RuleProxy" -c user.email="ruleproxy@users.noreply.github.com" commit -m "RuleProxy: 分应用/分端口代理工具（进程/文件夹/端口分流、开机自启动、托盘常驻、单实例、抗上游切换）" >nul 2>&1
if errorlevel 1 echo    (无变更或已提交，继续)

echo [2/4] 关联远程仓库 ...
git remote get-url origin >nul 2>&1 || git remote add origin https://github.com/%REPO%.git

echo [3/4] 推送源码到 GitHub ...
git push -u origin main 2>nul
if errorlevel 1 (
  echo    远程可能有文件，先合并再推送...
  git pull --rebase origin main >nul 2>&1
  git push -u origin main
  if errorlevel 1 (
    echo [错误] 推送失败。若默认分支是 master 请手动: git push -u origin HEAD:master
    exit /b 1
  )
)

echo [4/4] 发布安装包到 Releases ...
gh release create v0.1.0 "dist\RuleProxy-Setup.exe" --repo "%REPO%" --title "RuleProxy v0.1.0" --notes "分应用/分端口代理工具：进程/文件夹/端口分流、开机自启动、托盘常驻、单实例、抗上游切换。" >nul 2>&1
if errorlevel 1 echo   [提示] 该版本可能已发布，可跳过或用: gh release create v0.1.0 dist\RuleProxy-Setup.exe --repo %REPO%

echo.
echo ============================================================
echo  完成！源码仓库: https://github.com/%REPO%
echo  安装包下载: https://github.com/%REPO%/releases
echo ============================================================
exit /b 0
