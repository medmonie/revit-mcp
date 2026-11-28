@echo off
rem Runs the freshly built dist exe's client configuration against this PC's real settings (as install.bat does).
rem Where "revit" already points to the installed exe nothing is written. Output: tests\results\client_config.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\dist\RevitMcp\revit-mcp-server.exe" --configure-clients "%LOCALAPPDATA%\RevitMcpBridge\revit-mcp-server.exe" > "%~dp0results\client_config.txt" 2>&1
echo exit %ERRORLEVEL% >> "%~dp0results\client_config.txt"
