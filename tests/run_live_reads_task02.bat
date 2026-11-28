@echo off
rem Read-only test on Task 02 (steel connections). Output: tests\results\live_reads_task02.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0live_reads_task02.py" > "%~dp0results\live_reads_task02.txt" 2>&1
