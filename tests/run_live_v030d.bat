@echo off
rem Shared parameters real add / remove and the wall opening family at a duct crossing (scratch). Output: tests\results\live_v030d.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0live_v030d.py" > "%~dp0results\live_v030d.txt" 2>&1
