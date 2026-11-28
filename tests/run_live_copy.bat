@echo off
rem Live test (copy). Output: tests\results\live_copy.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0live_copy.py" > "%~dp0results\live_copy.txt" 2>&1
