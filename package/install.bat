@echo off
rem Revit MCP bridge installer. Installs the add-in for every Revit on this PC that the package was built for,
rem the MCP server (a single exe, no Python needed), and adds "revit" to Claude Desktop and Codex.
setlocal
cd /d "%~dp0"
set "LOG=%~dp0install_result.txt"
echo Revit MCP bridge install  %DATE% %TIME% > "%LOG%"

tasklist /fi "imagename eq Revit.exe" | find /i "Revit.exe" >nul && (
  echo Revit is running. Close Revit and run install.bat again.
  echo RESULT=STOPPED: Revit running >> "%LOG%"
  pause
  exit /b 1
)
if not exist "revit-mcp-server.exe" (
  echo revit-mcp-server.exe is missing next to install.bat. Unzip the whole package first.
  echo RESULT=FAILED: server exe missing >> "%LOG%"
  pause
  exit /b 1
)

rem Files from a downloaded zip carry the internet mark; Revit 2024 refuses to load such a DLL. Remove the mark.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -LiteralPath '%~dp0' -Recurse -File | Unblock-File" >> "%LOG%" 2>&1

rem ---------------------------------------------------------------- add-in, per Revit year
set "INSTALLED="
for /d %%D in ("addin\*") do call :addin %%~nxD
if not defined INSTALLED (
  echo No Revit 2024 or 2027 found on this PC. Install Revit, then run install.bat again.
  echo RESULT=FAILED: no matching Revit >> "%LOG%"
  pause
  exit /b 1
)

rem ---------------------------------------------------------------- server
set "SRV=%LOCALAPPDATA%\RevitMcpBridge"
if not exist "%SRV%" mkdir "%SRV%"
copy /y "revit-mcp-server.exe" "%SRV%\" >> "%LOG%" 2>&1 || (
  echo Could not copy the server to %SRV%. If an MCP client is running it, quit the client and try again.
  echo RESULT=FAILED: server copy >> "%LOG%"
  pause
  exit /b 1
)
set "EXE=%SRV%\revit-mcp-server.exe"
echo SERVER %EXE% >> "%LOG%"

rem Snippets for clients that are set up by hand (ChatGPT desktop, other PCs).
> "%SRV%\claude_desktop_config_snippet.json" echo { "mcpServers": { "revit": { "command": "%EXE:\=\\%" } } }
> "%SRV%\codex_mcp_config_snippet.toml" echo [mcp_servers.revit]
>> "%SRV%\codex_mcp_config_snippet.toml" echo command = "%EXE:\=\\%"

rem ---------------------------------------------------------------- MCP clients
echo.
echo Connecting MCP clients:
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0configure_clients.ps1" -Exe "%EXE%"
echo CLIENTS configure_clients.ps1 exit %ERRORLEVEL% >> "%LOG%"
echo RESULT=OK >> "%LOG%"

echo.
echo Installed.
echo   Add-in for Revit:%INSTALLED%
echo   Server: %EXE%
echo.
echo Next:
echo   1. Fully quit Claude Desktop (tray icon ^> Quit) and start it again.
echo   2. Start Revit. When it asks about loading "Revit MCP bridge", choose Always Load.
echo   3. Open a model and ask Claude: "Ping Revit and tell me which model is open."
echo ChatGPT desktop: Settings ^> MCP servers ^> Add server, name revit, STDIO, command = the server path above.
pause
exit /b 0

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
