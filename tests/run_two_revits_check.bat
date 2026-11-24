@echo off
rem Live check of several open Revit sessions. Appends to tests\results\two_revits.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0live_two_revits.py" check >> "%~dp0results\two_revits.txt" 2>&1
