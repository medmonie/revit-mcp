@echo off
rem Pushes this folder to https://github.com/medmonie/revit-mcp in one commit.
rem   publish_to_github.bat                 commit message from tools\commit_message.txt
rem The first run links this folder to GitHub (the repository is public, so fetching needs no sign-in); the first
rem push opens a GitHub sign-in window once (Git Credential Manager), later pushes are silent.
rem .gitignore keeps out build output, dist, .venv, Autodesk DLLs and machine-specific files. Log: publish_result.txt
setlocal
cd /d "%~dp0"
set "LOG=%~dp0publish_result.txt"
echo Revit MCP publish  %DATE% %TIME% > "%LOG%"

where git >nul 2>nul || (echo GIT_MISSING >> "%LOG%" & echo Git is not installed: https://git-scm.com/download/win & pause & exit /b 1)

if not exist ".git" git init -b main >> "%LOG%" 2>&1
git remote get-url origin >nul 2>nul || git remote add origin https://github.com/medmonie/revit-mcp.git
git remote set-url origin https://github.com/medmonie/revit-mcp.git
rem Commit the files exactly as they are (no line-ending conversion), and identify the author.
git config core.autocrlf false
git config user.name >nul 2>nul || git config user.name "Mohamed A. Hamed"
git config user.email >nul 2>nul || git config user.email "medmonie@users.noreply.github.com"

rem Start from what is on GitHub (keeps its history), then record this folder's state on top of it.
git fetch origin main >> "%LOG%" 2>&1 || (echo FETCH_FAILED >> "%LOG%" & goto failed)
git reset --mixed origin/main >> "%LOG%" 2>&1 || goto failed
git add -A >> "%LOG%" 2>&1 || goto failed
echo ---- changed files >> "%LOG%"
git diff --cached --name-status >> "%LOG%" 2>&1
git diff --cached --quiet && (echo NOTHING_TO_COMMIT >> "%LOG%" & echo Nothing changed since the last push. & echo RESULT=OK >> "%LOG%" & pause & exit /b 0)
git commit -F "%~dp0tools\commit_message.txt" >> "%LOG%" 2>&1 || goto failed
git push origin HEAD:main >> "%LOG%" 2>&1 || goto failed
git log -1 --oneline >> "%LOG%" 2>&1
echo RESULT=OK >> "%LOG%"
echo Pushed to https://github.com/medmonie/revit-mcp
pause
exit /b 0

:failed
echo RESULT=FAILED >> "%LOG%"
echo Publishing failed. See publish_result.txt
pause
exit /b 1
