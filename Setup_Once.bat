@echo off
setlocal
cd /d "%~dp0"
title Study setup (run once)

set "PY=python"
if exist "%USERPROFILE%\anaconda3\python.exe" set "PY=%USERPROFILE%\anaconda3\python.exe"

echo ============================================================
echo  ONE-TIME SETUP FOR THIS LAPTOP
echo ============================================================
echo.
echo [1/2] Installing the Python packages the study needs...
"%PY%" -m pip install pylsl bleak numpy matplotlib pillow scipy
if errorlevel 1 (
    echo.
    echo ERROR: package install failed. Check the internet connection and run this file again.
    pause
    exit /b 1
)

echo.
echo [2/2] Allowing the headset to reach this laptop (Windows firewall, port 8765)...
net session >nul 2>&1
if errorlevel 1 (
    echo.
    echo SKIPPED: this step needs administrator rights.
    echo Close this window, right-click Setup_Once.bat and choose "Run as administrator".
    pause
    exit /b 1
)
netsh advfirewall firewall delete rule name="Study bridge 8765" >nul 2>&1
netsh advfirewall firewall add rule name="Study bridge 8765" dir=in action=allow protocol=TCP localport=8765 profile=private,public >nul
echo Done.
echo.
echo Setup finished. You do not need to run this file again.
pause
