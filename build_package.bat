@echo off
rem Builds the distributables in dist\ (log: package_build.txt):
rem   RevitMcp.zip         installer: add-in for Revit 2024 + 2027, revit-mcp-server.exe, install.bat / uninstall.bat
rem   RevitMcp-source.zip  clean source tree for building on another PC (no bin, obj, .venv, dist, refs, logs)
rem Needs on THIS machine only: .NET 10 SDK, Python 3.10+ with server\.venv (run setup_server.bat once).
rem A Revit year is built against its installed Revit, else against refs\<year>\RevitAPI.dll + RevitAPIUI.dll
rem (build.bat copies them there). A year that cannot be built here is taken from plugin\prebuilt\<year>\ when present
rem (each package build refreshes that folder for the years it built). The log says, per year, which source was used.
setlocal
cd /d "%~dp0"
set "LOG=%~dp0package_build.txt"
echo Revit MCP package build  %DATE% %TIME% > "%LOG%"
if exist dist rmdir /s /q dist
set "OUT=dist\RevitMcp"
mkdir "%OUT%\addin"

echo [1/4] add-in >> "%LOG%"
set FOUND=0
set "MISSING="
for %%Y in (2024 2027) do call :release %%Y
if "%FOUND%"=="0" goto fail

echo [2/4] server exe >> "%LOG%"
if not exist "server\.venv\Scripts\python.exe" (echo server\.venv missing: run setup_server.bat first >> "%LOG%" & goto fail)
server\.venv\Scripts\python -m pip install -r server\requirements.txt pyinstaller >> "%LOG%" 2>&1 || goto fail
server\.venv\Scripts\python -m PyInstaller --onefile --noconfirm --name revit-mcp-server --collect-all mcp --paths server ^
  --hidden-import catalog --hidden-import bridge_client --hidden-import excel_io --hidden-import qs_report ^
  --hidden-import bbs_report --hidden-import xlsx_util --collect-submodules openpyxl ^
  --distpath "%OUT%" --workpath dist\_work --specpath dist\_work server\server.py >> "%LOG%" 2>&1 || goto fail
if not exist "%OUT%\revit-mcp-server.exe" goto fail
rem The exe must start and answer the MCP handshake; the smoke test does that against the Python sources already.

echo [3/4] installer zip >> "%LOG%"
for %%F in (install.bat uninstall.bat configure_clients.ps1 check_user.ps1 INSTALL.txt) do copy /y "package\%%F" "%OUT%\" >> "%LOG%" 2>&1 || goto fail
for %%F in (README.md CHANGELOG.md LICENSE.md) do copy /y "%%F" "%OUT%\" >> "%LOG%" 2>&1 || goto fail
powershell -NoProfile -Command "Compress-Archive -Path dist\RevitMcp\* -DestinationPath dist\RevitMcp.zip -Force" >> "%LOG%" 2>&1 || goto fail

echo [4/4] source zip >> "%LOG%"
set "SRC=dist\_src\revit-mcp"
robocopy . "%SRC%" /E /NFL /NDL /NJH /NJS /XD dist bin obj .venv __pycache__ .git .vs results refs prebuilt ^
  /XF local.props build_result.txt setup_result.txt package_build.txt publish_result.txt install_result.txt release_result.txt ^
  claude_config_snippet.json codex_config_snippet.toml publish_to_github.bat commit_message.txt *.user *.suo *.zip *.old* >> "%LOG%" 2>&1
if errorlevel 8 goto fail
powershell -NoProfile -Command "Compress-Archive -Path dist\_src\revit-mcp -DestinationPath dist\RevitMcp-source.zip -Force" >> "%LOG%" 2>&1 || goto fail
rmdir /s /q dist\_src
rmdir /s /q dist\_work

if defined MISSING (echo RESULT=OK_PARTIAL missing:%MISSING% >> "%LOG%") else (echo RESULT=OK >> "%LOG%")
echo Package built: dist\RevitMcp.zip  (see package_build.txt)
exit /b 0
:fail
echo RESULT=FAILED >> "%LOG%"
echo Package build failed: see package_build.txt
exit /b 1

rem :release <year> - build that year's add-in into the package: fresh build first, else plugin\prebuilt\<year>.
:release
set "Y=%1"
set "API="
if exist "C:\Program Files\Autodesk\Revit %Y%\RevitAPI.dll" set "API=C:\Program Files\Autodesk\Revit %Y%"
if not defined API if exist "refs\%Y%\RevitAPI.dll" if exist "refs\%Y%\RevitAPIUI.dll" set "API=%~dp0refs\%Y%"
set "DEST=%OUT%\addin\%Y%"
if defined API (
  if exist "plugin\bin\%Y%" rmdir /s /q "plugin\bin\%Y%"
  dotnet build plugin -c Release --no-incremental -p:RevitYear=%Y% -p:DeployAddin=false "-p:RevitDir=%API%\\" >> "%LOG%" 2>&1
  if exist "plugin\bin\%Y%\RevitMcpBridge.dll" (
    mkdir "%DEST%\RevitMcpBridge" 2>nul
    copy /y "plugin\bin\%Y%\RevitMcpBridge.dll" "%DEST%\RevitMcpBridge\" >> "%LOG%" 2>&1
    copy /y "plugin\RevitMcpBridge.addin" "%DEST%\" >> "%LOG%" 2>&1
    if exist "plugin\prebuilt\%Y%" rmdir /s /q "plugin\prebuilt\%Y%"
    mkdir "plugin\prebuilt\%Y%" 2>nul
    copy /y "plugin\bin\%Y%\RevitMcpBridge.dll" "plugin\prebuilt\%Y%\" >> "%LOG%" 2>&1
    echo RELEASE %Y%: built here against %API% >> "%LOG%"
    set /a FOUND+=1
    goto :eof
  )
  echo RELEASE %Y%: build FAILED, see above >> "%LOG%"
)
if exist "plugin\prebuilt\%Y%\RevitMcpBridge.dll" (
  mkdir "%DEST%\RevitMcpBridge" 2>nul
  copy /y "plugin\prebuilt\%Y%\RevitMcpBridge.dll" "%DEST%\RevitMcpBridge\" >> "%LOG%" 2>&1
  copy /y "plugin\RevitMcpBridge.addin" "%DEST%\" >> "%LOG%" 2>&1
  echo RELEASE %Y%: NOT built here, using plugin\prebuilt\%Y% - check it is from this version >> "%LOG%"
  set /a FOUND+=1
  goto :eof
)
echo RELEASE %Y%: MISSING - no Revit %Y% API found and no prebuilt DLL >> "%LOG%"
set "MISSING=%MISSING% %Y%"
goto :eof
