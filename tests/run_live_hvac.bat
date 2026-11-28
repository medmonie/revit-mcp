@echo off
rem Live test (MEP / HVAC, phase 18). Output: tests\results\live_hvac.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0live_hvac.py" > "%~dp0results\live_hvac.txt" 2>&1
