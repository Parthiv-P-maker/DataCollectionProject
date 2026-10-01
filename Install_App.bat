@echo off
setlocal
cd /d "%~dp0"
title Install the study app on the headset

set "ADB=adb"
where adb >nul 2>&1
if errorlevel 1 set "ADB=C:\Program Files\Unity\Hub\Editor\6000.4.8f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"

set "APK="
for /f "delims=" %%f in ('dir /b /o-d "build\*.apk" 2^>nul') do if not defined APK set "APK=build\%%f"
if not defined APK (
    echo No app file found in the "build" folder.
    pause
    exit /b 1
)

echo Installing %APK% on the headset...
echo (Plug the headset in, put it on and choose "Allow" if it asks about USB debugging.)
"%ADB%" install -r "%APK%"
echo.
echo NOTE: this keeps data already stored on the headset. Do NOT uninstall the app
echo between participants - uninstalling deletes all saved results.
pause
