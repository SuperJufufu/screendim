# 卸载 ScreenDim：停止守护进程、恢复正常亮度、删除开机自启

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$cli  = Join-Path $here 'ScreenDimCLI.exe'

Write-Host "停止守护进程 ..."
if (Test-Path $cli) { & $cli --stop | Out-Null }
Start-Sleep -Milliseconds 600
Get-Process ScreenDim -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

Write-Host "强制恢复正常亮度 ..."
if (Test-Path $cli) { & $cli --restore-saved }

$lnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'ScreenDim.lnk'
if (Test-Path $lnk) { Remove-Item $lnk -Force; Write-Host "已删除 $lnk" }
else { Write-Host "未找到开机自启快捷方式" }

Write-Host ""
Write-Host "当前亮度:"
if (Test-Path $cli) { & $cli --list | Select-String 'brightness' | ForEach-Object { $_.Line.Trim() } }
