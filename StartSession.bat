@echo off
setlocal
cd /d "%~dp0"
title Study launcher

rem ---- Settings -------------------------------------------------------------
rem The last 8 characters of the name printed on the Polar H10 strap / sensor.
set "STRAP=1B77F535"
rem ---------------------------------------------------------------------------

set "PY=python"
if exist "%USERPROFILE%\anaconda3\python.exe" set "PY=%USERPROFILE%\anaconda3\python.exe"

"%PY%" -c "import pylsl, bleak" >nul 2>&1
if errorlevel 1 (
    echo The study software is not installed on this laptop yet.
    echo Right-click Setup_Once.bat, choose "Run as administrator", then try again.
    pause
    exit /b 1
)

rem The Excite-O-Meter app would take the strap's connection, so close it.
taskkill /im ExciteOMeter.exe /f >nul 2>&1

for /f "delims=" %%i in ('powershell -NoProfile -ExecutionPolicy Bypass -File "Tools\get_laptop_ip.ps1"') do set "LAPTOP_IP=%%i"
for /f "delims=" %%i in ('powershell -NoProfile -Command "Get-Date -Format yyyy-MM-dd_HHmm"') do set "STAMP=%%i"

echo ============================================================
echo  STARTING THE STUDY SOFTWARE
echo ============================================================
echo.
echo Starting the heart-rate strap connection (window "Study - Polar H10")...
start "Study - Polar H10" cmd /k ""%PY%" "Tools\polar_h10_lsl.py" --device %STRAP%"

echo Waiting a few seconds for the strap...
timeout /t 8 /nobreak >nul

echo Starting the headset link (window "Study - Bridge")...
start "Study - Bridge" cmd /k ""%PY%" "Tools\physiology_lsl_bridge.py" --quest-port 8765 --backup-dir "bridge_recordings\%STAMP%""

echo.
echo ============================================================
echo  READY. Two new windows are open. Leave them open.
echo.
echo  This laptop's address:   %LAPTOP_IP%
echo.
echo  In the headset, the System Check page shows "Laptop IP".
echo  If it is different from the address above, choose
echo  "Change laptop IP" and type the address above.
echo.
echo  Window "Study - Polar H10" should show live  HR ... bpm  lines.
echo  Window "Study - Bridge" should say  Quest connected  once the
echo  app is open on the headset.
echo.
echo  When the session is finished, run FinishSession.bat.
echo ============================================================
echo.
pause
