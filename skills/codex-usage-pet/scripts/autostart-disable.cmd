@echo off
setlocal

set "RUN_KEY=HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
set "RUN_NAME=CodexUsagePetAutoStart"
set "LEGACY_RUN_NAME=CodexUsagePetWatcher"

for %%I in ("%~dp0..\..\..") do set "PLUGIN_ROOT=%%~fI"
set "BUNDLED_WATCHER=%PLUGIN_ROOT%\bin\CodexUsagePetWatcher.exe"

if defined LOCALAPPDATA (
  set "RUNTIME_WATCHER=%LOCALAPPDATA%\CodexUsagePet\runtime\bin\CodexUsagePetWatcher.exe"
) else (
  set "RUNTIME_WATCHER="
)

call :delete_run_value "%RUN_NAME%"
if errorlevel 1 goto registry_failed
call :delete_run_value "%LEGACY_RUN_NAME%"
if errorlevel 1 goto registry_failed

rem Stop the installed runtime watcher first. A bundled watcher is the fallback
rem for first-time installs and older layouts. position.txt is never removed.
if not defined RUNTIME_WATCHER goto stop_bundled
if not exist "%RUNTIME_WATCHER%" goto stop_bundled
"%RUNTIME_WATCHER%" --stop >nul 2>&1
if not errorlevel 1 goto watcher_stopped

:stop_bundled
if not exist "%BUNDLED_WATCHER%" goto watcher_stopped
"%BUNDLED_WATCHER%" --stop >nul 2>&1
if errorlevel 1 goto stop_failed

:watcher_stopped

echo Codex Usage Pet autostart is disabled.
exit /b 0

:delete_run_value
reg.exe query "%RUN_KEY%" /v "%~1" >nul 2>&1
if errorlevel 1 exit /b 0
reg.exe delete "%RUN_KEY%" /v "%~1" /f >nul 2>&1
exit /b %errorlevel%

:registry_failed
echo The Codex Usage Pet autostart registry value could not be removed. 1>&2
exit /b 2

:stop_failed
echo The Codex Usage Pet watcher could not be stopped. 1>&2
exit /b 3
