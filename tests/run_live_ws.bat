@echo off
rem Live test (ws). Output: tests\results\live_ws.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0live_ws.py" > "%~dp0results\live_ws.txt" 2>&1
