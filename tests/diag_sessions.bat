@echo off
rem Lists the bridge connection files and running Revit processes. Output: tests\results\diag_sessions.txt
if not exist "%~dp0results" mkdir "%~dp0results"
set "OUT=%~dp0results\diag_sessions.txt"
echo %DATE% %TIME% > "%OUT%"
echo LOCALAPPDATA=%LOCALAPPDATA% >> "%OUT%"
dir /a /s "%LOCALAPPDATA%\RevitMcpBridge" >> "%OUT%" 2>&1
echo ---- bridge.json >> "%OUT%"
type "%LOCALAPPDATA%\RevitMcpBridge\bridge.json" >> "%OUT%" 2>&1
echo. >> "%OUT%"
for %%F in ("%LOCALAPPDATA%\RevitMcpBridge\sessions\*.json") do (echo ---- %%~nxF >> "%OUT%" & type "%%F" >> "%OUT%" & echo. >> "%OUT%")
echo ---- Revit processes >> "%OUT%"
tasklist /fi "imagename eq Revit.exe" >> "%OUT%" 2>&1
echo ---- server processes >> "%OUT%"
tasklist /fi "imagename eq revit-mcp-server.exe" /v >> "%OUT%" 2>&1
wmic process where "name='revit-mcp-server.exe'" get ProcessId,ExecutablePath,CommandLine >> "%OUT%" 2>&1
echo ---- Claude packages >> "%OUT%"
dir /b "%LOCALAPPDATA%\Packages" 2>nul | findstr /i claude >> "%OUT%"
