@echo off
setlocal

cd /d "%~dp0"

where git >nul 2>nul
if errorlevel 1 (
  echo Git is not installed or not in PATH.
  pause
  exit /b 1
)

for /f "delims=" %%i in ('git branch --show-current') do set "BRANCH=%%i"
if "%BRANCH%"=="" set "BRANCH=main"

set "MESSAGE=%~1"
if "%MESSAGE%"=="" (
  for /f "usebackq delims=" %%i in (`powershell -NoProfile -Command "Get-Date -Format 'yyyy-MM-dd HH:mm:ss'"`) do set "STAMP=%%i"
  set "MESSAGE=Update %BRANCH% %STAMP%"
)

echo Staging changes...
git add -A
if errorlevel 1 (
  echo Failed to stage changes.
  pause
  exit /b 1
)

git diff --cached --quiet
if errorlevel 1 (
  echo Creating commit: %MESSAGE%
  git commit -m "%MESSAGE%"
  if errorlevel 1 (
    echo Commit failed.
    pause
    exit /b 1
  )
) else (
  echo No new changes to commit.
)

echo Pushing to origin/%BRANCH%...
git push origin %BRANCH%
if errorlevel 1 (
  echo Push failed.
  pause
  exit /b 1
)

echo.
echo Done.
pause
exit /b 0
