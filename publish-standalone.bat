@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"
echo Сборка для Windows x64 со встроенной средой .NET 8
dotnet publish src\YoungScientistDiary.App -c Release --self-contained true -p:RestoreLockedMode=true -o dist\YoungScientistDiary-standalone
if errorlevel 1 goto fail
copy /y LICENSE dist\YoungScientistDiary-standalone\LICENSE >nul
if errorlevel 1 goto fail
copy /y NOTICE dist\YoungScientistDiary-standalone\NOTICE >nul
if errorlevel 1 goto fail
copy /y README.md dist\YoungScientistDiary-standalone\README.md >nul
if errorlevel 1 goto fail
xcopy /y /i /e licenses dist\YoungScientistDiary-standalone\licenses >nul
if errorlevel 1 goto fail
xcopy /y /i /e docs dist\YoungScientistDiary-standalone\docs >nul
if errorlevel 1 goto fail
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File scripts\Copy-RuntimeNotices.ps1 -PublishDirectory dist\YoungScientistDiary-standalone -ProjectFile src\YoungScientistDiary.App\YoungScientistDiary.App.csproj -Configuration Release -RuntimeIdentifier win-x64
if errorlevel 1 goto fail
echo Готово: dist\YoungScientistDiary-standalone\YoungScientistDiary.exe
echo Переносите всю папку. Отдельная установка .NET Runtime не требуется.
if /i not "%~1"=="--no-pause" pause
exit /b 0
:fail
echo Публикация завершилась с ошибкой. Проверьте сообщение выше, .NET 8 SDK, PowerShell и доступ к NuGet.
if /i not "%~1"=="--no-pause" pause
exit /b 1
