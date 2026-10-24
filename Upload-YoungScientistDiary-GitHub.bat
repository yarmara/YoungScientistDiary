@echo off
setlocal EnableExtensions DisableDelayedExpansion

rem Upload the contents of this folder to the existing GitHub main branch.
rem No force push, no remote file deletion, no git history rewrite.
rem Keep this BAT next to YoungScientistDiary.sln.

set "REPO_URL=https://github.com/yarmara/YoungScientistDiary.git"
set "BRANCH=main"

rem %~dp0 ends with '\'. Quoting that value directly in ROBOCOPY can
rem escape the closing quote and merge two arguments. Strip the final '\'.
set "SOURCE=%~dp0"
if "%SOURCE:~-1%"=="\" set "SOURCE=%SOURCE:~0,-1%"
rem At a drive root, use C:\. rather than the ambiguous relative path C:.
if "%SOURCE:~-1%"==":" set "SOURCE=%SOURCE%\."

set "WORK=%TEMP%\YoungScientistDiary_GitHub_%RANDOM%_%RANDOM%"

 echo ============================================================
 echo   YoungScientistDiary - GitHub source upload (fixed)
 echo ============================================================
 echo.
 echo Source: "%SOURCE%"
 echo Remote: %REPO_URL%
 echo Branch: %BRANCH%
 echo.

where git >nul 2>&1
if errorlevel 1 (
    echo ERROR: Git for Windows is not installed or not in PATH.
    echo https://git-scm.com/downloads/win
    goto :fail
)

if not exist "%SOURCE%\YoungScientistDiary.sln" (
    echo ERROR: YoungScientistDiary.sln was not found next to this BAT.
    echo Place this script in the extracted source project root.
    goto :fail
)

 echo [1/5] Checking access to the existing GitHub branch...
 git ls-remote --exit-code --heads "%REPO_URL%" "%BRANCH%" >nul
 if errorlevel 1 (
    echo ERROR: Cannot read the remote branch.
    echo Check internet, repository address and GitHub authentication.
    goto :fail
 )

 echo [2/5] Cloning main into a temporary work directory...
 git clone --quiet --single-branch --branch "%BRANCH%" "%REPO_URL%" "%WORK%"
 if errorlevel 1 (
    echo ERROR: Git clone failed.
    goto :fail_keep
 )

 echo [3/5] Copying source files...
 rem /E copies directories without deleting remote files.
 rem /XJ prevents traversing directory junctions.
 rem ROBOCOPY returns 0..7 for success and 8+ for errors.
 robocopy "%SOURCE%" "%WORK%" /E /XJ /R:1 /W:1 /NP /NFL /NDL /NJH /NJS ^
   /XD ".git" ".vs" ".idea" ".vscode" ".continue" "bin" "obj" "dist" "publish" "TestResults" "node_modules" "Backups" "backups" ^
   /XF "*.db" "*.db-shm" "*.db-wal" "*.sqlite" "*.sqlite3" "*.bak" "*.tmp" "*.log" "*.zip" "*.7z" "*.pfx" "*.p12" "*.pem" "*.key" ".env" ".env.*" "*.user" "*.suo"
 if errorlevel 8 (
    echo ERROR: ROBOCOPY could not copy the source files.
    goto :fail_keep
 )

 rem Add basic .NET exclusions only if the source had no .gitignore.
 if not exist "%WORK%\.gitignore" (
    > "%WORK%\.gitignore" echo # Generated files and local secrets
    >> "%WORK%\.gitignore" echo **/bin/
    >> "%WORK%\.gitignore" echo **/obj/
    >> "%WORK%\.gitignore" echo .vs/
    >> "%WORK%\.gitignore" echo .idea/
    >> "%WORK%\.gitignore" echo dist/
    >> "%WORK%\.gitignore" echo publish/
    >> "%WORK%\.gitignore" echo TestResults/
    >> "%WORK%\.gitignore" echo *.db
    >> "%WORK%\.gitignore" echo *.db-shm
    >> "%WORK%\.gitignore" echo *.db-wal
    >> "%WORK%\.gitignore" echo *.sqlite
    >> "%WORK%\.gitignore" echo *.sqlite3
    >> "%WORK%\.gitignore" echo *.log
    >> "%WORK%\.gitignore" echo *.bak
    >> "%WORK%\.gitignore" echo *.user
    >> "%WORK%\.gitignore" echo *.suo
    >> "%WORK%\.gitignore" echo .env
    >> "%WORK%\.gitignore" echo .env.*
    >> "%WORK%\.gitignore" echo *.pfx
    >> "%WORK%\.gitignore" echo *.p12
    >> "%WORK%\.gitignore" echo *.pem
    >> "%WORK%\.gitignore" echo *.key
 )

 echo [4/5] Preparing change list...
 git -C "%WORK%" add -A
 if errorlevel 1 (
    echo ERROR: git add failed.
    goto :fail_keep
 )

 git -C "%WORK%" diff --cached --quiet
 if errorlevel 2 (
    echo ERROR: Could not inspect staged changes.
    goto :fail_keep
 )
 if not errorlevel 1 (
    echo There are no changes to publish.
    goto :success
 )

 echo.
 echo Files staged for upload:
 git -C "%WORK%" diff --cached --stat
 echo.
 git -C "%WORK%" diff --cached --name-only
 echo.
 echo WARNING: This repository is PUBLIC. Inspect the file names above.
 set "CONFIRM="
 set /p "CONFIRM=Type YES to commit and push: "
 if /I not "%CONFIRM%"=="YES" (
    echo Upload cancelled by user.
    goto :fail_keep
 )

 echo.
 echo [5/5] Creating commit and pushing to GitHub...
 git -C "%WORK%" commit -m "Update YoungScientistDiary source code"
 if errorlevel 1 (
    echo ERROR: Git commit failed. Check git user.name and user.email.
    goto :fail_keep
 )

 git -C "%WORK%" push origin "%BRANCH%"
 if errorlevel 1 (
    echo ERROR: Git push failed. It could be authentication or remote changes.
    echo The temporary Git worktree remains available at:
    echo "%WORK%"
    goto :fail_keep
 )

:success
 echo.
 echo SUCCESS: GitHub main has been updated.
 echo https://github.com/yarmara/YoungScientistDiary
 if exist "%WORK%\.git" rmdir /S /Q "%WORK%" >nul 2>&1
 echo.
 pause
 exit /b 0

:fail_keep
 echo.
 echo Temporary Git worktree was preserved if it exists:
 echo "%WORK%"
:fail
 echo.
 echo Upload was NOT completed.
 echo.
 pause
 exit /b 1
