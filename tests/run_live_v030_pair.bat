@echo off
rem Both 0.30.0 live tests in one Revit session. Output: tests\results\live_v030_pair.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0live_v030_pair.py" > "%~dp0results\live_v030_pair.txt" 2>&1
