@echo off
rem 0.22.0 live test (task04). Output: tests\results\live_p10_task04.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0live_p10.py" task04 > "%~dp0results\live_p10_task04.txt" 2>&1
