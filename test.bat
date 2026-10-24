@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"
dotnet run --project tests\YoungScientistDiary.SelfTests -c Release
set "YSD_TEST_EXIT=%ERRORLEVEL%"
if /i not "%~1"=="--no-pause" pause
exit /b %YSD_TEST_EXIT%
