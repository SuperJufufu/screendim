# Builds ScreenDim into two binaries from one source file:
#   ScreenDim.exe     - winexe, no console window ever, this is the daemon
#   ScreenDimCLI.exe  - console, for --list / --selftest / --status / --dim-now
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }

$src = Join-Path $here 'ScreenDim.cs'
$ui  = Join-Path $here 'ui.html'
if (-not (Test-Path $ui)) { throw "ui.html missing at $ui" }
# csc wants /out and /target BEFORE any source file
$common = @('/nologo', '/platform:x64', '/optimize+', '/warn:4',
            '/reference:System.dll', '/reference:System.Drawing.dll',
            "/resource:$ui,ScreenDim.ui.html")

Write-Host "compiling ScreenDim.exe (daemon, no console window) ..."
& $csc @common '/target:winexe' "/out:$(Join-Path $here 'ScreenDim.exe')" $src
if ($LASTEXITCODE -ne 0) { throw "csc failed for ScreenDim.exe (exit $LASTEXITCODE)" }

Write-Host "compiling ScreenDimCLI.exe (console) ..."
& $csc @common '/target:exe' "/out:$(Join-Path $here 'ScreenDimCLI.exe')" $src
if ($LASTEXITCODE -ne 0) { throw "csc failed for ScreenDimCLI.exe (exit $LASTEXITCODE)" }

Get-ChildItem (Join-Path $here 'ScreenDim*.exe') |
    Select-Object Name, @{n='KB';e={[math]::Round($_.Length/1KB,1)}}, LastWriteTime |
    Format-Table -AutoSize
