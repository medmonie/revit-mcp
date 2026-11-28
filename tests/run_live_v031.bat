@echo off
rem 0.31.0 live test (phase 17 P1, architecture) in a scratch project. Output: tests\results\live_v031.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0live_v031.py" > "%~dp0results\live_v031.txt" 2>&1
