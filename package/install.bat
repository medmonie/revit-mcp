@echo off
rem Revit MCP bridge installer. Installs the add-in for every Revit on this PC that the package was built for,
rem the MCP server (a single exe, no Python needed), and adds "revit" to Claude Desktop and Codex.
setlocal
cd /d "%~dp0"
set "LOG=%~dp0install_result.txt"
echo Revit MCP bridge install  %DATE% %TIME% > "%LOG%"
rem Options: /this_user (install for the account running this window), /claude_open (do not stop for Claude Desktop),
rem /no_pause (never wait for a key: for scripted installs)
set "THIS_USER=" & set "CLAUDE_OPEN=" & set "NO_PAUSE="
for %%A in (%*) do (
  if /i "%%~A"=="/this_user" set "THIS_USER=1"
  if /i "%%~A"=="/claude_open" set "CLAUDE_OPEN=1"
  if /i "%%~A"=="/no_pause" set "NO_PAUSE=1"
)

tasklist /fi "imagename eq Revit.exe" | find /i "Revit.exe" >nul && (
  echo Revit is running. Close Revit and run install.bat again.
  echo RESULT=STOPPED: Revit running >> "%LOG%"
  if not defined NO_PAUSE pause
  exit /b 1
)
rem Claude Desktop keeps its settings in memory and can write them back over the "revit" entry added here; it also
rem holds the server exe open. Quit it first (tray icon > Quit), then start it again after installing.
if not defined CLAUDE_OPEN tasklist /fi "imagename eq claude.exe" | find /i "claude.exe" >nul && (
  echo Claude is running ^(Claude Desktop, or Claude Code: also claude.exe^). Quit Claude Desktop completely
  echo ^(tray icon ^> Quit^), then run install.bat again.
  echo It is started again after the install. ^(install.bat /claude_open installs anyway.^)
  echo RESULT=STOPPED: Claude Desktop running >> "%LOG%"
  if not defined NO_PAUSE pause
  exit /b 1
)
if not exist "revit-mcp-server.exe" (
  echo revit-mcp-server.exe is missing next to install.bat. Unzip the whole package first.
  echo RESULT=FAILED: server exe missing >> "%LOG%"
  if not defined NO_PAUSE pause
  exit /b 1
)

rem The files go into the profile of the account running this script; stop when that is not the desktop user
rem (e.g. "Run as administrator" with another account's credentials). install.bat /this_user skips the check.
echo Installing for %USERDOMAIN%\%USERNAME%  (%USERPROFILE%)
echo ACCOUNT %USERDOMAIN%\%USERNAME% >> "%LOG%"
if defined THIS_USER goto :user_ok
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0check_user.ps1" >> "%LOG%" 2>&1
if errorlevel 2 goto :other_user
:user_ok

rem Files from a downloaded zip carry the internet mark; Revit 2024 refuses to load such a DLL. Remove the mark.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -LiteralPath '%~dp0' -Recurse -File | Unblock-File" >> "%LOG%" 2>&1

rem ---------------------------------------------------------------- add-in, per Revit year
set "INSTALLED="
for /d %%D in ("addin\*") do call :addin %%~nxD
if not defined INSTALLED (
  echo No Revit 2024 or 2027 found on this PC. Install Revit, then run install.bat again.
  echo RESULT=FAILED: no matching Revit >> "%LOG%"
  if not defined NO_PAUSE pause
  exit /b 1
)

rem ---------------------------------------------------------------- server
set "SRV=%LOCALAPPDATA%\RevitMcpBridge"
if not exist "%SRV%" mkdir "%SRV%"
copy /y "revit-mcp-server.exe" "%SRV%\" >> "%LOG%" 2>&1 || (
  echo Could not copy the server to %SRV%. If an MCP client is running it, quit the client and try again.
  echo RESULT=FAILED: server copy >> "%LOG%"
  if not defined NO_PAUSE pause
  exit /b 1
)
set "EXE=%SRV%\revit-mcp-server.exe"
echo SERVER %EXE% >> "%LOG%"

rem Snippets for clients set up by hand (other PCs). Codex and the ChatGPT desktop app share ~/.codex/config.toml.
> "%SRV%\claude_desktop_config_snippet.json" echo { "mcpServers": { "revit": { "command": "%EXE:\=\\%" } } }
> "%SRV%\codex_mcp_config_snippet.toml" echo [mcp_servers.revit]
>> "%SRV%\codex_mcp_config_snippet.toml" echo command = "%EXE:\=\\%"
>> "%SRV%\codex_mcp_config_snippet.toml" echo startup_timeout_sec = 30
>> "%SRV%\codex_mcp_config_snippet.toml" echo tool_timeout_sec = 3600
>> "%SRV%\codex_mcp_config_snippet.toml" echo.
>> "%SRV%\codex_mcp_config_snippet.toml" echo [mcp_servers.revit.env]
>> "%SRV%\codex_mcp_config_snippet.toml" echo REVIT_MCP_SPLIT = "1"

rem ---------------------------------------------------------------- MCP clients
echo.
echo Connecting MCP clients:
rem Done by the server exe (Python): unlike a PowerShell script it is not blocked by company script policies.
set "CLIENTS=%TEMP%\revitmcp_clients_%RANDOM%.txt"
"%~dp0revit-mcp-server.exe" --configure-clients "%EXE%" > "%CLIENTS%" 2>&1
set "CRC=%ERRORLEVEL%"
type "%CLIENTS%"
type "%CLIENTS%" >> "%LOG%"
del /q "%CLIENTS%" 2>nul
echo CLIENTS exit %CRC% >> "%LOG%"
set "CLIENTS_OK=1"
if not "%CRC%"=="0" (
  set "CLIENTS_OK="
  echo.
  echo Some MCP client settings could not be changed ^(see above^). Add the revit entry by hand:
  echo   %SRV%\claude_desktop_config_snippet.json shows it.
)
echo RESULT=OK >> "%LOG%"
if not defined CLIENTS_OK echo NOTE: MCP client settings not (all) changed - add the revit entry by hand >> "%LOG%"

echo.
echo Installed.
echo   For user: %USERDOMAIN%\%USERNAME%
echo   Add-in for Revit:%INSTALLED%
echo   Server: %EXE%
echo.
echo Next:
echo   1. Fully quit Claude Desktop (tray icon ^> Quit) and start it again.
echo   2. Start Revit. When it asks about loading "Revit MCP bridge", choose Always Load.
echo   3. Open a model and ask Claude: "Ping Revit and tell me which model is open."
echo ChatGPT desktop app: it reads the Codex settings above ^(~/.codex/config.toml^); restart it to see revit under Settings ^> MCP servers.
if not defined NO_PAUSE pause
exit /b 0

:other_user
echo.
echo This window runs as %USERDOMAIN%\%USERNAME%, but the desktop belongs to another user - see install_result.txt.
echo The add-in and the server would go into %USERPROFILE%, where Revit for the desktop user does not look.
echo Close this window and double-click install.bat as the user who runs Revit. It needs no administrator rights,
echo so do not use Run as administrator. To install for %USERNAME% on purpose, run:  install.bat /this_user
echo RESULT=STOPPED: running as another account than the desktop user >> "%LOG%"
if not defined NO_PAUSE pause
exit /b 1

:addin
set "Y=%1"
if not exist "C:\Program Files\Autodesk\Revit %Y%\Revit.exe" if not exist "%APPDATA%\Autodesk\Revit\Addins\%Y%" (
  echo SKIPPED %Y%: Revit %Y% not installed >> "%LOG%"
  goto :eof
)
set "DEST=%APPDATA%\Autodesk\Revit\Addins\%Y%"
if not exist "%DEST%\RevitMcpBridge" mkdir "%DEST%\RevitMcpBridge"
copy /y "addin\%Y%\RevitMcpBridge\RevitMcpBridge.dll" "%DEST%\RevitMcpBridge\" >> "%LOG%" 2>&1 || (echo FAILED %Y%: dll copy >> "%LOG%" & goto :eof)
copy /y "addin\%Y%\RevitMcpBridge.addin" "%DEST%\" >> "%LOG%" 2>&1 || (echo FAILED %Y%: manifest copy >> "%LOG%" & goto :eof)
echo INSTALLED %Y% to %DEST% >> "%LOG%"
set "INSTALLED=%INSTALLED% %Y%"
goto :eof
