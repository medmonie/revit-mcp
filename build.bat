@echo off
rem Builds the Revit add-in for every installed Revit (2027 and 2024) and deploys it to that Revit's Addins folder.
rem Log: build_result.txt     Needs the .NET 10 SDK; the 2024 build also downloads one small NuGet package once.
rem Revit may stay open: the loaded DLL is moved aside after a successful compile. Restart Revit to load the new one.
setlocal
set "ROOT=%~dp0"
set "LOG=%ROOT%build_result.txt"
cd /d "%ROOT%plugin"
if errorlevel 1 exit /b 1
echo Revit MCP bridge build  %DATE% %TIME% > "%LOG%"

set "FAILED=0"
set "BUILT=0"
for %%Y in (2027 2024) do call :build %%Y

echo. >> "%LOG%"
echo ================ Revit running? >> "%LOG%"
tasklist /fi "imagename eq Revit.exe" /fo csv /nh >> "%LOG%" 2>&1
if "%BUILT%"=="0" (
  echo No installed Revit target was built. See build_result.txt
  exit /b 1
)
if not "%FAILED%"=="0" (
  echo Build or deployment failed. See build_result.txt
  exit /b 1
)
echo Build and deployment completed. See build_result.txt
exit /b 0

:build
set "Y=%1"
set "RVT=C:\Program Files\Autodesk\Revit %Y%"
echo. >> "%LOG%"
echo ================ Revit %Y% >> "%LOG%"
if not exist "%RVT%\RevitAPI.dll" (
  echo SKIPPED_%Y%: "%RVT%\RevitAPI.dll" not found >> "%LOG%"
  goto :eof
)
rem Reference copies of the two Revit API DLLs, so that Claude can compile-check the sources against this exact Revit.
if not exist "%ROOT%refs\%Y%" mkdir "%ROOT%refs\%Y%"
copy /y "%RVT%\RevitAPI.dll" "%ROOT%refs\%Y%\" >nul 2>&1
copy /y "%RVT%\RevitAPIUI.dll" "%ROOT%refs\%Y%\" >nul 2>&1

set "BUILT=1"
set "D=%APPDATA%\Autodesk\Revit\Addins\%Y%\RevitMcpBridge"
rem MSBuild copies the new DLL only after compilation succeeds and reports copy failures.
dotnet build -c Release --no-incremental -p:RevitYear=%Y% >> "%LOG%" 2>&1
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" set "FAILED=1"
echo EXIT_%Y%=%RESULT% >> "%LOG%"
dir "%APPDATA%\Autodesk\Revit\Addins\%Y%\RevitMcpBridge*" >> "%LOG%" 2>&1
dir "%D%" >> "%LOG%" 2>&1
goto :eof
