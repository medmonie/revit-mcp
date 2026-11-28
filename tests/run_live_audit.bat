@echo off
rem 0.18.1 API-alignment live test (Task 04: reads and dry runs only). Output: tests\results\live_audit.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0live_audit.py" > "%~dp0results\live_audit.txt" 2>&1
