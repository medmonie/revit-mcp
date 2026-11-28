@echo off
rem Revit 2027, one session: live_v030b, live_v030, live_p10 scratch and task04 (dry runs). Output: tests\results\live_2027_chain.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0live_2027_chain.py" > "%~dp0results\live_2027_chain.txt" 2>&1
