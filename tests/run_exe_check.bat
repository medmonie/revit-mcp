@echo off
rem Starts the installed server exe over stdio and pings Revit through it. Output: tests\results\exe_check.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0exe_check.py" > "%~dp0results\exe_check.txt" 2>&1
