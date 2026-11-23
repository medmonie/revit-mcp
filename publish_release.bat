@echo off
rem Publishes the GitHub release v<version> (version from server\catalog.py) with the zips from build_package.bat.
rem Run build_package.bat first, and publish_to_github.bat once (it stores the GitHub sign-in). Log: release_result.txt
setlocal
cd /d "%~dp0"
if not exist "dist\RevitMcp.zip" (echo dist\RevitMcp.zip is missing: run build_package.bat first. & pause & exit /b 1)
powershell -NoProfile -ExecutionPolicy Bypass -File "tools\publish_release.ps1" -Notes "tools\release_notes.md" -Asset "dist\RevitMcp.zip,dist\RevitMcp-source.zip" > release_result.txt 2>&1
set "RC=%ERRORLEVEL%"
if "%RC%"=="0" (echo RESULT=OK>> release_result.txt) else (echo RESULT=FAILED>> release_result.txt)
type release_result.txt
pause
exit /b %RC%
