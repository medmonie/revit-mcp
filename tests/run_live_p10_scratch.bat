@echo off
rem 0.22.0 live test (scratch). Output: tests\results\live_p10_scratch.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0live_p10.py" scratch > "%~dp0results\live_p10_scratch.txt" 2>&1
