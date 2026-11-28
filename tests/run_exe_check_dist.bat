@echo off
rem Like run_exe_check.bat, but checks the freshly built dist\RevitMcp\revit-mcp-server.exe (before installing it).
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0exe_check.py" "%~dp0..\dist\RevitMcp\revit-mcp-server.exe" > "%~dp0results\exe_check.txt" 2>&1
