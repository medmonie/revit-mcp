@echo off
rem Short STEP (2027) and gbXML check in a scratch. Output: tests\results\live_v030c.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0live_v030c.py" > "%~dp0results\live_v030c.txt" 2>&1
