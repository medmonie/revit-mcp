@echo off
rem 0.20.0 live test (scratch): numbering, containers, 2027 splices. Output: tests\results\live_p7b_scratch.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0live_p7b.py" scratch > "%~dp0results\live_p7b_scratch.txt" 2>&1
