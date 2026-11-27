@echo off
rem ESCaptureSketch - remove the .escs file type association (current user).
rem Works even if the program folder was already deleted or moved.
setlocal

echo.
echo ESCaptureSketch - remove .escs file association
echo ------------------------------------------------
reg query "HKCU\Software\Classes\.escs" /ve 2>nul | find "ESCaptureSketch.escs" >nul
if not errorlevel 1 reg delete "HKCU\Software\Classes\.escs" /ve /f >nul 2>&1
reg delete "HKCU\Software\Classes\.escs\OpenWithProgids" /v "ESCaptureSketch.escs" /f >nul 2>&1
reg query "HKCU\Software\Classes\ESCaptureSketch.escs" >nul 2>&1
if errorlevel 1 (
    echo File association was not registered. Nothing to remove.
) else (
    reg delete "HKCU\Software\Classes\ESCaptureSketch.escs" /f >nul 2>&1
    if errorlevel 1 (
        echo FAILED to remove the file association.
    ) else (
        echo Removed the .escs file association.
    )
)

echo.
echo Note: if ESCaptureSketch is started again with the association option on, it registers again.
echo.
pause
endlocal
