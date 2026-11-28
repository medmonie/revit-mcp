@echo off
rem Phase 7 live test (scratch). Output: tests\results\live_p7_scratch.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0live_p7.py" scratch > "%~dp0results\live_p7_scratch.txt" 2>&1
