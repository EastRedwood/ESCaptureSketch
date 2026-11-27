@echo off
rem ESCaptureSketch - remove the Windows startup entry.
rem Works even if the program folder was already deleted or moved.
rem Env var (for testing only): ESCAPTURESKETCH_AUTOSTART_NAME
setlocal

set "NAME=ESCaptureSketch"
if defined ESCAPTURESKETCH_AUTOSTART_NAME set "NAME=%ESCAPTURESKETCH_AUTOSTART_NAME%"

echo.
echo ESCaptureSketch - remove startup entry
echo ---------------------------------------
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "%NAME%" >nul 2>&1
if errorlevel 1 (
    echo Startup entry "%NAME%" was not registered. Nothing to remove.
) else (
    reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "%NAME%" /f >nul 2>&1
    if errorlevel 1 (
        echo FAILED to remove startup entry "%NAME%".
    ) else (
        echo Removed startup entry "%NAME%".
    )
)

echo.
echo Note: a running ESCaptureSketch keeps running until you close it (tray menu - Exit).
echo.
pause
endlocal
