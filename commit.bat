@echo off
rem Double-click to commit all current changes in this repo (no push).
cd /d "%~dp0"

git rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 (
    echo This folder is not a git repository.
    pause
    exit /b 1
)

echo ==== Changes to be committed ====
git status --short
echo.

for /f %%c in ('git status --porcelain ^| find /c /v ""') do set COUNT=%%c
if "%COUNT%"=="0" (
    echo Nothing to commit.
    pause
    exit /b 0
)

powershell -NoProfile -ExecutionPolicy Bypass -Command "$OutputEncoding = New-Object Text.UTF8Encoding $false; $m = Read-Host 'Commit message (just press Enter for an automatic one, close the window to cancel)'; if ([string]::IsNullOrWhiteSpace($m)) { $m = 'Update ' + (Get-Date -Format 'yyyy-MM-dd HH:mm') }; git add -A; if ($LASTEXITCODE -ne 0) { exit 1 }; $m | git commit -F -; exit $LASTEXITCODE"

echo.
if errorlevel 1 (
    echo Commit FAILED. See the messages above.
) else (
    echo Commit done. Nothing was pushed.
)
echo.
git log --oneline -3
echo.
pause
