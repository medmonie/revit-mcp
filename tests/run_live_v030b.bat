@echo off
rem 0.30.0 live test, part 2: parts, arrays, alignment, Performance Adviser, watch, preview, pick, colour fill, dimension types,
rem 2027 sheet collections and STEP, ACC parameters, CAD link reload, DGN and gbXML. Output: tests\results\live_v030b.txt
if not exist "%~dp0results" mkdir "%~dp0results"
"%~dp0..\server\.venv\Scripts\python" "%~dp0live_v030b.py" > "%~dp0results\live_v030b.txt" 2>&1
