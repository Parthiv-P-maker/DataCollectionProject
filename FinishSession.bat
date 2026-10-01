@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"
title Study - finish session

rem ---- Settings -------------------------------------------------------------
set "PKG=com.DefaultCompany.VRTemplate"
rem ---------------------------------------------------------------------------

set "PY=python"
if exist "%USERPROFILE%\anaconda3\python.exe" set "PY=%USERPROFILE%\anaconda3\python.exe"

set "ADB=adb"
where adb >nul 2>&1
if errorlevel 1 set "ADB=C:\Program Files\Unity\Hub\Editor\6000.4.8f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
if not exist "%ADB%" if /i not "%ADB%"=="adb" (
    echo Cannot find the "adb" tool. Ask the researcher to install it.
    pause
    exit /b 1
)

echo ============================================================
echo  FINISHING THE SESSION
echo ============================================================
echo.
echo Closing the heart-rate and headset-link windows...
taskkill /fi "WINDOWTITLE eq Study - Polar H10*" /f /t >nul 2>&1
taskkill /fi "WINDOWTITLE eq Study - Bridge*" /f /t >nul 2>&1
taskkill /fi "WINDOWTITLE eq Study - Live heart rate*" /f /t >nul 2>&1

echo.
echo Looking for the headset (it must be plugged in with the USB cable)...
set "FOUND="
for /f "skip=1 tokens=1,2" %%a in ('"%ADB%" devices') do if "%%b"=="device" set "FOUND=1"
if not defined FOUND (
    echo.
    echo HEADSET NOT FOUND.
    echo  - Plug the headset into the laptop with the USB cable.
    echo  - Put the headset on and choose "Allow" if it asks about USB debugging.
    echo  - Then run FinishSession.bat again.
    pause
    exit /b 1
)

if not exist "CollectedData" mkdir "CollectedData"
echo Copying the results from the headset...
"%ADB%" pull "/sdcard/Android/data/%PKG%/files/ExperimentData" "CollectedData"
if errorlevel 1 (
    echo.
    echo Could not copy the results. Has at least one session been completed on this headset?
    pause
    exit /b 1
)

echo.
echo Creating the pictures (heatmaps and heart-rate charts)...
"%PY%" Tools\make_visuals.py "CollectedData\ExperimentData"

echo.
echo Finding eye fixations...
"%PY%" Tools\gaze_fixations.py "CollectedData\ExperimentData"

echo.
echo ============================================================
echo  DONE. Your results are in the folder:
echo    %~dp0CollectedData\ExperimentData
echo  Each participant has their own folder (P001, P002, ...).
echo ============================================================
explorer "%~dp0CollectedData\ExperimentData"
pause
