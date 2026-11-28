@echo off
rem Removes the Revit MCP bridge: the add-in from every Revit year, the server, and the "revit" entry of Claude
rem Desktop and Codex (their other servers and settings are kept; each file is backed up first).
setlocal
cd /d "%~dp0"
tasklist /fi "imagename eq Revit.exe" | find /i "Revit.exe" >nul && (echo Close Revit first, then run uninstall.bat again. & pause & exit /b 1)

echo Uninstalling for %USERDOMAIN%\%USERNAME%  (%USERPROFILE%)
set "THIS_USER="
for %%A in (%*) do if /i "%%~A"=="/this_user" set "THIS_USER=1"
if defined THIS_USER goto :user_ok
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0check_user.ps1" >nul 2>&1
if errorlevel 2 (echo This window runs as %USERNAME%, not as the desktop user. Run uninstall.bat as the user who runs Revit, without Run as administrator; or uninstall.bat /this_user. & pause & exit /b 1)
:user_ok
for /d %%Y in ("%APPDATA%\Autodesk\Revit\Addins\*") do (
  if exist "%%Y\RevitMcpBridge.addin" del /q "%%Y\RevitMcpBridge.addin"
  if exist "%%Y\RevitMcpBridge" rmdir /s /q "%%Y\RevitMcpBridge"
)
rem Remove the "revit" entry with the package's server exe (not blocked by script policies); else the installed one.
set "TOOL=%~dp0revit-mcp-server.exe"
if not exist "%TOOL%" set "TOOL=%LOCALAPPDATA%\RevitMcpBridge\revit-mcp-server.exe"
if exist "%TOOL%" ("%TOOL%" --remove-clients) else (powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0configure_clients.ps1" -Remove)
set "SRV=%LOCALAPPDATA%\RevitMcpBridge"
del /q "%SRV%\revit-mcp-server.exe" "%SRV%\claude_desktop_config_snippet.json" "%SRV%\codex_mcp_config_snippet.toml" 2>nul
echo.
echo Uninstalled. The audit log stays in %SRV% (delete the folder if you do not need it).
echo Restart Claude Desktop. In ChatGPT desktop, remove the revit server under Settings ^> MCP servers.
pause
