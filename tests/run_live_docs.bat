@echo off
rem Live test (documents, phase 16). Output: tests\results\live_docs.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0live_docs.py" > "%~dp0results\live_docs.txt" 2>&1
