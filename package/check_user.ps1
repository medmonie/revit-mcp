# Which Windows account install.bat / uninstall.bat runs as, and whether it is the account that owns this desktop.
# The add-in and the server go into the running account's profile (%APPDATA%, %LOCALAPPDATA%), and Revit loads
# add-ins from the desktop user's profile. Running the script elevated under another account (UAC "Run as
# administrator" with an admin's credentials, common on domain controllers) would install for that account instead.
# Exit 2 when the two accounts differ, else 0.
$me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
Write-Output "ACCOUNT $me (profile $env:USERPROFILE)"
$owner = $null
try {
    $session = (Get-Process -Id $PID).SessionId
    $explorer = Get-CimInstance Win32_Process -Filter "Name='explorer.exe' AND SessionId=$session" -ErrorAction Stop | Select-Object -First 1
    if ($explorer) {
        $o = Invoke-CimMethod -InputObject $explorer -MethodName GetOwner -ErrorAction Stop
        if ($o.ReturnValue -eq 0) { $owner = "$($o.Domain)\$($o.User)" }
    }
} catch { }
if ($owner) { Write-Output "DESKTOP USER $owner" } else { Write-Output "DESKTOP USER unknown (not checked)" }
if ($owner -and ($owner -ne $me)) { exit 2 }
exit 0
