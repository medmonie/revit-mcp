@echo off
rem Live test of phases 8 and 9 (task04 mode). Output: tests\results\live_p89_task04.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0live_p89.py" task04 > "%~dp0results\live_p89_task04.txt" 2>&1
