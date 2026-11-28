@echo off
rem Live test (MEP / HVAC, phase 18 third part). Output: tests\results\live_hvac3.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0live_hvac3.py" > "%~dp0results\live_hvac3.txt" 2>&1
