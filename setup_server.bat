@echo off
rem One-time setup of the MCP server: creates server\.venv, installs the 'mcp' package and writes the block to add
rem to Claude's configuration (claude_config_snippet.json). Log: setup_result.txt     Needs Python 3.10+ and internet.
setlocal
cd /d "%~dp0"
set "LOG=%~dp0setup_result.txt"
echo Revit MCP server setup  %DATE% %TIME% > "%LOG%"

if exist "server\.venv\Scripts\python.exe" goto :install
py -3 -m venv "server\.venv" >> "%LOG%" 2>&1
if exist "server\.venv\Scripts\python.exe" goto :install
python -m venv "server\.venv" >> "%LOG%" 2>&1
if exist "server\.venv\Scripts\python.exe" goto :install
for /d %%P in ("%LOCALAPPDATA%\Python\pythoncore-*") do if not exist "server\.venv\Scripts\python.exe" "%%P\python.exe" -m venv "server\.venv" >> "%LOG%" 2>&1
if exist "server\.venv\Scripts\python.exe" goto :install
echo RESULT=FAILED: Python was not found >> "%LOG%"
echo Python was not found. Install Python 3.10 or later, then run this again.
pause
exit /b 1

:install
"server\.venv\Scripts\python.exe" -m pip install -r "server\requirements.txt" >> "%LOG%" 2>&1
if errorlevel 1 (
  echo RESULT=FAILED: pip install >> "%LOG%"
  echo pip install failed, see setup_result.txt
  pause
  exit /b 1
)

set "SNIP=%CD%\claude_config_snippet.json"
"server\.venv\Scripts\python.exe" "tools\write_client_configs.py" >> "%LOG%" 2>&1
if errorlevel 1 (
  echo RESULT=FAILED: configuration generation >> "%LOG%"
  echo Configuration generation failed. See setup_result.txt
  exit /b 1
)
type "%SNIP%" >> "%LOG%"
echo RESULT=OK >> "%LOG%"
echo.
echo Server ready. Add the "revit" entry of this file to Claude's configuration, next to "civil3d":
echo   %SNIP%
echo (Claude desktop: Settings ^> Developer ^> Edit Config), then restart Claude completely.
echo For local Codex, merge codex_config_snippet.toml into your Codex config.toml.
echo.
type "%SNIP%"
pause
