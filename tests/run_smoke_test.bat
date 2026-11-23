@echo off
rem Offline test of the MCP server against a fake Revit. Output: tests\results\smoke_test.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0smoke_test.py" > "%~dp0results\smoke_test.txt" 2>&1
