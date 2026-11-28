@echo off
rem Read-only survey of the detached models folder. Output: tests\results\survey_folder.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0survey_folder.py" "C:\Users\mohamed.hamed\Desktop\New folder\detached\detached" > "%~dp0results\survey_folder.txt" 2>&1
