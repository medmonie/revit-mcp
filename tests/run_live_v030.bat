@echo off
rem 0.30.0 live test: revit_addins, analytical model, HVAC / system zones, staged exports, close guard, document identity, audit, UI queue.
rem Output: tests\results\live_v030.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0live_v030.py" > "%~dp0results\live_v030.txt" 2>&1
