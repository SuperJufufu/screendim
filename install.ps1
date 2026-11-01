# 安装 ScreenDim 开机自启（放入当前用户的启动文件夹，无需管理员权限）

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe  = Join-Path $here 'ScreenDim.exe'

if (-not (Test-Path $exe)) { throw "找不到 $exe，请先运行 build.ps1" }

$idle = 300         # 空闲多少秒后进省电模式（5 分钟）
$port = 8765        # 网页控制面板端口
$daemonArgs = "--daemon --idle $idle --level 0 --mode dim --hz 60 --saver-plan saver --port $port --wake mouse --threshold 4"

$startup = [Environment]::GetFolderPath('Startup')
$lnk     = Join-Path $startup 'ScreenDim.lnk'

$sh = New-Object -ComObject WScript.Shell
$sc = $sh.CreateShortcut($lnk)
$sc.TargetPath       = $exe
$sc.Arguments        = $daemonArgs
$sc.WorkingDirectory = $here
$sc.WindowStyle      = 7          # 最小化（该 exe 是 winexe，本来就没有控制台窗口）
$sc.Description      = 'ScreenDim - idle 后把显示器背光调到 0，帧缓冲保持不变'
$sc.Save()

Write-Host "已创建开机自启快捷方式: $lnk"

# ---- 桌面快捷方式「省电专家」：双击打开网页控制面板 ----
$desktop = [Environment]::GetFolderPath('Desktop')
$expert  = Join-Path $desktop '省电专家.lnk'
$sd = $sh.CreateShortcut($expert)
$sd.TargetPath       = $exe
$sd.Arguments        = "$daemonArgs --open-ui"
$sd.WorkingDirectory = $here
$sd.WindowStyle      = 7
$sd.Description      = '省电专家 - 屏幕/CPU/GPU/内存/功率监控与一键省电'
# 图标由 make-icon.ps1 生成；文件不在就退回 exe 自带图标
$iconPath = Join-Path $here 'power.ico'
$sd.IconLocation     = if (Test-Path $iconPath) { "$iconPath,0" } else { "$exe,0" }
$sd.Save()
Write-Host "已创建桌面快捷方式: $expert"
Write-Host "  双击它 = 启动守护进程并打开 http://127.0.0.1:$port/"
Write-Host "参数: $daemonArgs --open-ui"

# 立刻启动一份
Get-Process ScreenDim -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600
Start-Process -FilePath $exe -ArgumentList $daemonArgs
Start-Sleep -Seconds 3
Write-Host ""
Write-Host "已启动。当前状态:"
& (Join-Path $here 'ScreenDimCLI.exe') --status
