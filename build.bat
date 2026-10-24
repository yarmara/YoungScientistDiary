@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"
echo Дневник молодого ученого — сборка для Windows x64 / .NET 8
where dotnet >nul 2>nul
if errorlevel 1 goto missing
dotnet --version
if errorlevel 1 goto fail
dotnet restore YoungScientistDiary.sln --locked-mode
if errorlevel 1 goto fail
dotnet build YoungScientistDiary.sln -c Release --no-restore
if errorlevel 1 goto fail
dotnet run --project tests\YoungScientistDiary.SelfTests -c Release --no-build
if errorlevel 1 goto fail
dotnet publish src\YoungScientistDiary.App -c Release --no-restore --self-contained false -o dist\YoungScientistDiary
if errorlevel 1 goto fail
copy /y LICENSE dist\YoungScientistDiary\LICENSE >nul
if errorlevel 1 goto fail
copy /y NOTICE dist\YoungScientistDiary\NOTICE >nul
if errorlevel 1 goto fail
copy /y README.md dist\YoungScientistDiary\README.md >nul
if errorlevel 1 goto fail
xcopy /y /i /e licenses dist\YoungScientistDiary\licenses >nul
if errorlevel 1 goto fail
xcopy /y /i /e docs dist\YoungScientistDiary\docs >nul
if errorlevel 1 goto fail
echo.
echo Готово: dist\YoungScientistDiary\YoungScientistDiary.exe
echo Папку dist\YoungScientistDiary нужно переносить целиком.
if /i not "%~1"=="--no-pause" pause
exit /b 0
:missing
echo Не найден dotnet. Установите .NET 8 SDK для Windows x64.
echo Инструкция: docs\BUILD.md
goto fail
:fail
echo.
echo Сборка или проверка завершилась с ошибкой. Смотрите сообщение выше.
if /i not "%~1"=="--no-pause" pause
exit /b 1
