@echo off
rem Live test (MEP / HVAC, phase 18 fourth part: accessories, insulation). Output: tests\results\live_hvac4.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0live_hvac4.py" > "%~dp0results\live_hvac4.txt" 2>&1
