@echo off
setlocal enabledelayedexpansion
rem Double-click to push the current branch to its remote (never forces).
cd /d "%~dp0"

git rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 (
    echo This folder is not a git repository.
    pause
    exit /b 1
)

for /f "delims=" %%b in ('git rev-parse --abbrev-ref HEAD') do set BRANCH=%%b
git remote get-url origin >nul 2>&1
if errorlevel 1 (
    echo No remote named "origin" is configured.
    pause
    exit /b 1
)

echo Branch : %BRANCH%
for /f "delims=" %%u in ('git remote get-url origin') do echo Remote : origin  %%u
echo.

git status --porcelain | find /c /v "" >"%TEMP%\da_dirty.txt"
set /p DIRTY=<"%TEMP%\da_dirty.txt"
del "%TEMP%\da_dirty.txt" >nul 2>&1
if not "%DIRTY%"=="0" (
    echo NOTE: %DIRTY% files have uncommitted changes. They are NOT pushed - run commit.bat first if you want them included.
    echo.
)

git rev-parse --abbrev-ref --symbolic-full-name @{u} >nul 2>&1
if errorlevel 1 (
    echo This branch has no upstream yet. It will be published as origin/%BRANCH%.
    echo.
    git log --oneline -10
    set PUSHARGS=-u origin %BRANCH%
) else (
    git fetch origin >nul 2>&1
    for /f %%n in ('git rev-list --count @{u}..HEAD') do set AHEAD=%%n
    for /f %%n in ('git rev-list --count HEAD..@{u}') do set BEHIND=%%n
    if not "!BEHIND!"=="0" (
        echo The remote has !BEHIND! commits you do not have locally, so a normal push would be rejected.
        echo Run "git pull --rebase" first, then run this again. Nothing was pushed.
        echo.
        pause
        exit /b 1
    )
    if "!AHEAD!"=="0" (
        echo Nothing to push, already up to date with the remote.
        echo.
        pause
        exit /b 0
    )
    echo Commits to push: !AHEAD!
    echo.
    git log --oneline @{u}..HEAD
    set PUSHARGS=
)

echo.
echo Press any key to push, or close this window to cancel.
pause >nul
echo.

git push !PUSHARGS!

echo.
if errorlevel 1 (
    echo Push FAILED. See the messages above.
) else (
    echo Push done.
)
echo.
pause
