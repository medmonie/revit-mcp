@echo off
rem Builds the distributables in dist\ (log: package_build.txt):
rem   RevitMcp.zip         installer: add-in for Revit 2024 + 2027, revit-mcp-server.exe, install.bat / uninstall.bat
rem   RevitMcp-source.zip  clean source tree for building on another PC (no bin, obj, .venv, dist, refs, logs)
rem Needs on THIS machine only: .NET 10 SDK, Python 3.10+ with server\.venv (run setup_server.bat once).
rem A Revit year is built against its installed Revit, else against refs\<year>\RevitAPI.dll + RevitAPIUI.dll
rem (build.bat copies them there). A year that cannot be built here is taken from plugin\prebuilt\<year>\ only when its
rem manifest (manifest.txt: version and SHA-256 of the DLL) matches this version and the DLL; each package build
rem refreshes that folder for the years it built. The log says, per year, which source was used.
rem Everything is built in dist\_staging and checked first (the exe is started and probed over stdio against a fake
rem Revit); only then does it replace the previous dist\ contents. A failed build leaves the last good dist\ untouched.
setlocal
cd /d "%~dp0"
set "LOG=%~dp0package_build.txt"
echo Revit MCP package build  %DATE% %TIME% > "%LOG%"
if not exist "server\.venv\Scripts\python.exe" (echo server\.venv missing: run setup_server.bat first >> "%LOG%" & goto fail)
set "PY=server\.venv\Scripts\python.exe"
set "VER="
for /f "usebackq delims=" %%V in (`%PY% -c "import sys; sys.path.insert(0, 'server'); import catalog; print(catalog.BRIDGE_VERSION)"`) do set "VER=%%V"
if not defined VER (echo could not read the version from server\catalog.py >> "%LOG%" & goto fail)
echo version %VER% >> "%LOG%"
set "STAGE=dist\_staging"
if exist "%STAGE%" rmdir /s /q "%STAGE%"
set "OUT=%STAGE%\RevitMcp"
mkdir "%OUT%\addin"

echo [1/5] add-in >> "%LOG%"
set FOUND=0
set "MISSING="
for %%Y in (2024 2027) do call :release %%Y || goto fail
if "%FOUND%"=="0" goto fail

echo [2/5] server exe >> "%LOG%"
%PY% -m pip install -r server\requirements.txt pyinstaller >> "%LOG%" 2>&1 || goto fail
%PY% -m PyInstaller --onefile --noconfirm --name revit-mcp-server --collect-all mcp --paths server ^
  --hidden-import catalog --hidden-import bridge_client --hidden-import excel_io --hidden-import qs_report ^
  --hidden-import bbs_report --hidden-import mep_report --hidden-import xlsx_util --collect-submodules openpyxl ^
  --distpath "%OUT%" --workpath "%STAGE%\_work" --specpath "%STAGE%\_work" server\server.py >> "%LOG%" 2>&1 || goto fail
if not exist "%OUT%\revit-mcp-server.exe" goto fail

echo [3/5] probe the staged exe (handshake, version %VER%, ping through a fake Revit, validation, clean exit) >> "%LOG%"
%PY% tools\probe_exe.py "%OUT%\revit-mcp-server.exe" >> "%LOG%" 2>&1 || goto fail

echo [4/5] installer zip >> "%LOG%"
for %%F in (install.bat uninstall.bat configure_clients.ps1 check_user.ps1 INSTALL.txt) do copy /y "package\%%F" "%OUT%\" >> "%LOG%" 2>&1 || goto fail
for %%F in (README.md CHANGELOG.md LICENSE.md) do copy /y "%%F" "%OUT%\" >> "%LOG%" 2>&1 || goto fail
powershell -NoProfile -Command "Compress-Archive -Path '%OUT%\*' -DestinationPath '%STAGE%\RevitMcp.zip' -Force" >> "%LOG%" 2>&1 || goto fail

echo [5/5] source zip >> "%LOG%"
set "SRC=%STAGE%\_src\revit-mcp"
robocopy . "%SRC%" /E /NFL /NDL /NJH /NJS /XD dist bin obj .venv __pycache__ .git .vs results refs prebuilt ^
  /XF local.props build_result.txt setup_result.txt package_build.txt publish_result.txt install_result.txt release_result.txt ^
  claude_config_snippet.json codex_config_snippet.toml publish_to_github.bat commit_message.txt *.user *.suo *.zip *.old* >> "%LOG%" 2>&1
if errorlevel 8 goto fail
powershell -NoProfile -Command "Compress-Archive -Path '%STAGE%\_src\revit-mcp' -DestinationPath '%STAGE%\RevitMcp-source.zip' -Force" >> "%LOG%" 2>&1 || goto fail
rmdir /s /q "%STAGE%\_src"
rmdir /s /q "%STAGE%\_work"

rem promote: the previous build is moved aside, the new one moved in, and the previous one restored if a move fails
echo promote >> "%LOG%"
if exist dist\_previous rmdir /s /q dist\_previous
mkdir dist\_previous
for %%F in (RevitMcp RevitMcp.zip RevitMcp-source.zip) do if exist "dist\%%F" move "dist\%%F" "dist\_previous\" >> "%LOG%" 2>&1 || goto restore
for %%F in (RevitMcp RevitMcp.zip RevitMcp-source.zip) do move "%STAGE%\%%F" "dist\" >> "%LOG%" 2>&1 || goto restore
rmdir /s /q dist\_previous
rmdir /s /q "%STAGE%"

if defined MISSING (echo RESULT=OK_PARTIAL missing:%MISSING% >> "%LOG%") else (echo RESULT=OK >> "%LOG%")
echo Package built: dist\RevitMcp.zip  (see package_build.txt)
exit /b 0
:restore
echo promotion failed: a window or program has a file open in dist\ (often a paused install window); close it and build again. Restoring the previous build >> "%LOG%"
for %%F in (RevitMcp RevitMcp.zip RevitMcp-source.zip) do if exist "dist\_previous\%%F" (
  if exist "dist\%%F" if exist "%STAGE%" move "dist\%%F" "%STAGE%\" >> "%LOG%" 2>&1
  move "dist\_previous\%%F" "dist\" >> "%LOG%" 2>&1
)
:fail
echo RESULT=FAILED >> "%LOG%"
echo Package build failed: see package_build.txt (the previous dist\ is unchanged)
exit /b 1

rem :release <year> - build that year's add-in into the package: fresh build first, else a verified plugin\prebuilt\<year>.
:release
set "Y=%1"
set "API="
if exist "C:\Program Files\Autodesk\Revit %Y%\RevitAPI.dll" set "API=C:\Program Files\Autodesk\Revit %Y%"
if not defined API if exist "refs\%Y%\RevitAPI.dll" if exist "refs\%Y%\RevitAPIUI.dll" set "API=%~dp0refs\%Y%"
set "DEST=%OUT%\addin\%Y%"
set "PRE=plugin\prebuilt\%Y%"
if defined API (
  if exist "plugin\bin\%Y%" rmdir /s /q "plugin\bin\%Y%"
  dotnet build plugin -c Release --no-incremental -p:RevitYear=%Y% -p:DeployAddin=false "-p:RevitDir=%API%\\" >> "%LOG%" 2>&1
  if errorlevel 1 exit /b 1
  if exist "plugin\bin\%Y%\RevitMcpBridge.dll" (
    mkdir "%DEST%\RevitMcpBridge" 2>nul
    copy /y "plugin\bin\%Y%\RevitMcpBridge.dll" "%DEST%\RevitMcpBridge\" >> "%LOG%" 2>&1 || exit /b 1
    copy /y "plugin\RevitMcpBridge.addin" "%DEST%\" >> "%LOG%" 2>&1 || exit /b 1
    if exist "%PRE%" rmdir /s /q "%PRE%"
    mkdir "%PRE%" 2>nul
    copy /y "plugin\bin\%Y%\RevitMcpBridge.dll" "%PRE%\" >> "%LOG%" 2>&1 || exit /b 1
    powershell -NoProfile -Command "Set-Content -Path '%PRE%\manifest.txt' -Value @('%VER%', (Get-FileHash '%PRE%\RevitMcpBridge.dll' -Algorithm SHA256).Hash)" >> "%LOG%" 2>&1 || exit /b 1
    echo RELEASE %Y%: built here against %API% >> "%LOG%"
    set /a FOUND+=1
    exit /b 0
  )
  echo RELEASE %Y%: build FAILED, see above >> "%LOG%"
  exit /b 1
)
if exist "%PRE%\RevitMcpBridge.dll" (
  powershell -NoProfile -Command "$m = Get-Content '%PRE%\manifest.txt' -ErrorAction Stop; if ($m[0] -ne '%VER%' -or $m[1] -ne (Get-FileHash '%PRE%\RevitMcpBridge.dll' -Algorithm SHA256).Hash) { exit 1 }" >> "%LOG%" 2>&1
  if errorlevel 1 (
    echo RELEASE %Y%: plugin\prebuilt\%Y% is not from version %VER% or its DLL changed - refused; build it on a PC with Revit %Y% >> "%LOG%"
    exit /b 1
  )
  mkdir "%DEST%\RevitMcpBridge" 2>nul
  copy /y "%PRE%\RevitMcpBridge.dll" "%DEST%\RevitMcpBridge\" >> "%LOG%" 2>&1 || exit /b 1
  copy /y "plugin\RevitMcpBridge.addin" "%DEST%\" >> "%LOG%" 2>&1 || exit /b 1
  echo RELEASE %Y%: NOT built here, using plugin\prebuilt\%Y% ^(manifest: version %VER%, hash checked^) >> "%LOG%"
  set /a FOUND+=1
  exit /b 0
)
echo RELEASE %Y%: MISSING - no Revit %Y% API found and no prebuilt DLL >> "%LOG%"
set "MISSING=%MISSING% %Y%"
exit /b 0
