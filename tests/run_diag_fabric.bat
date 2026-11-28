@echo off
rem fabric_sheet_place diagnostic on Task 04 (dry runs). Output: tests\results\diag_fabric.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0diag_fabric.py" > "%~dp0results\diag_fabric.txt" 2>&1
