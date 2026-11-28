@echo off
rem Phase 6 live test (task04). Output: tests\results\live_p6_task04.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0live_p6.py" task04 > "%~dp0results\live_p6_task04.txt" 2>&1
