@echo off
rem Phase 7 live test (task04). Output: tests\results\live_p7_task04.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0live_p7.py" task04 > "%~dp0results\live_p7_task04.txt" 2>&1
