@echo off
rem Phase 6 live test (scratch). Output: tests\results\live_p6_scratch.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0live_p6.py" scratch > "%~dp0results\live_p6_scratch.txt" 2>&1
