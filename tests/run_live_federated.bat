@echo off
rem 0.21.0 READ-ONLY live test on the KTR15 federated model. Output: tests\results\live_federated.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0live_federated.py" > "%~dp0results\live_federated.txt" 2>&1
