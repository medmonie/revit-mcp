@echo off
rem One Revit session (2024 or 2027): live_v030d, live_p6 / live_p89 scratch (real) and task04 (dry runs). Output: tests\results\live_close_chain.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0live_close_chain.py" > "%~dp0results\live_close_chain.txt" 2>&1
