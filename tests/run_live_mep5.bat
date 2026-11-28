@echo off
rem Live test (MEP phase 18 rest: sprinklers, plumbing, sleeves, electrical, trays, report). Output: tests\results\live_mep5.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0live_mep5.py" > "%~dp0results\live_mep5.txt" 2>&1
