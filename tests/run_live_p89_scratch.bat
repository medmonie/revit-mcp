@echo off
rem Live test of phases 8 and 9 (scratch mode). Output: tests\results\live_p89_scratch.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0live_p89.py" scratch > "%~dp0results\live_p89_scratch.txt" 2>&1
