@echo off
setlocal
cd /d "%~dp0"
title Remove a participant

set "PY=python"
if exist "%USERPROFILE%\anaconda3\python.exe" set "PY=%USERPROFILE%\anaconda3\python.exe"

echo ============================================================
echo  REMOVE A FAULTY PARTICIPANT
echo ============================================================
echo  Plug the headset in first (put it on and press Allow if asked),
echo  so the participant is removed from the headset as well.
echo.
"%PY%" Tools\remove_participant.py --list
echo.
set "IDS="
set /p IDS=Type the participant number(s) to REMOVE, for example P003 (or press Enter to cancel):
if "%IDS%"=="" (
    echo Nothing was changed.
    pause
    exit /b 0
)
echo.
"%PY%" Tools\remove_participant.py %IDS%
echo.
pause
