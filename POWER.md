# 这台机器的省电改动清单

记录为了省电对系统做的**所有**改动、实测效果和回滚方法。
（ScreenDim 本体见 [README.md](README.md)）

快照时间：2026-10-07

---

## 总览

| # | 改动 | 实测效果 | 回滚 |
|---|---|---|---|
| 1 | ScreenDim：空闲 5 分钟 → 背光 0 + 刷新率 60Hz + 切到**节能**电源方案 | GPU 58.5W → 39.3W（−19.2W），背光另省面板 15~30W，CPU 侧再省一截 | `.\uninstall.ps1` |
| 2 | CPU 最低处理器状态 100% → 5% | 空闲核心 88.7% → 65.2% 标称频率 | 见下 |
| 3 | Edge 硬件加速（原本是关的） | Edge CPU 87.1% → 29.9%，GPU 56.6W → 42W | 见下 |

改动 2、3 之后整机 GPU 从 56.6W 降到 ~40.8W，CPU 侧另有一大块。

> 改动 1 的电源方案切换是**运行时**行为：活动方案是"节能"还是"卓越性能"取决于当前是否处于省电态。
> 鼠标一动就切回卓越性能，所以醒着的时候你感觉不到差别。

---

## 1. ScreenDim

```powershell
cd C:\Users\Administrator\Desktop\ScreenDim
.\ScreenDimCLI.exe --status      # 看状态（含当前电源方案）
.\ScreenDimCLI.exe --plan-list   # 列出所有电源方案与活动方案
.\install.ps1                    # 重装/改参数后重新应用
.\uninstall.ps1                  # 完全卸载（停止 + 恢复 + 删自启）
```

**桌面快捷方式「省电专家」**（由 install.ps1 创建）：双击打开网页控制面板
`http://127.0.0.1:8765/`，可以看屏幕/CPU/GPU/内存/功率，并一键开关省电模式与自动省电。

**开机自启**：`启动` 文件夹里的 `ScreenDim.lnk`。

当前参数（写在启动文件夹快捷方式里）：

```
--daemon --idle 300 --level 0 --mode dim --hz 60 --saver-plan saver --wake mouse --threshold 4
```

进入省电时依次做三件事（顺序有讲究）：

| 步骤 | 内容 | 为什么这个顺序 |
|---|---|---|
| 1 | DDC 背光 → 0 | 先让屏黑掉，后面模式切换的闪屏就看不见 |
| 2 | 刷新率 → 60Hz | 此时屏已全黑，重协商不可见 |
| 3 | 电源方案 → 节能 | 瞬时完成，没人在等 |

唤醒时顺序**反过来**（原因见 README：模式切换后立刻做 DDC 写入会被阻塞重试约 2.4 秒）：

| 步骤 | 内容 | 实测 |
|---|---|---|
| 1 | 电源方案 → 卓越性能 | 瞬时 |
| 2 | 背光恢复 | ~0.47 s（屏幕可见） |
| 3 | 刷新率恢复 | ~1.4 s（伴随一次重同步闪屏） |

三项都有独立的恢复记录（`state.txt` / `state-hz.txt` / `state-plan.txt`），原子写入，
崩溃/强杀后下次启动自动还原。**实测**：模拟"灭屏时崩溃 + 显示器被重新编号"，
重启后亮度和刷新率都正确还原。

改空闲时长：编辑 `install.ps1` 里的 `$idle`，再跑一次。

**注意**：它用 `SetThreadExecutionState(ES_DISPLAY_REQUIRED)` 阻止 Windows 关屏/休眠/屏保/自动锁屏，
这是为了保证截图和桌面识别程序 7×24 能正常工作。如果你希望机器能自动睡眠，加 `--allow-sleep`。

**节能方案的代价**：`saver`（节能）会限制 CPU 频率和核心停泊，**后台程序在省电期间也会被限速**。
截图/桌面识别仍能工作，但吞吐会下降。想保守一点用 `--saver-plan balanced`。

---

## 2. CPU 最低处理器状态（电源计划）

**问题**：活动方案是"卓越性能"，`PROCTHROTTLEMIN = 100%`，导致 CPU 在 86% 空闲时仍被钉在
149% 标称频率跑。这是纯浪费。

**改动**：交流电下最低处理器状态 100% → 5%（最高仍是 100%，**性能不受影响**）。

```powershell
# 查看
powercfg /query SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN

# 回滚
powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN 100
powercfg /setactive SCHEME_CURRENT

# 再次应用
powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN 5
powercfg /setactive SCHEME_CURRENT
```

实测（A/B）：最低状态 100% 时最空闲核心跑 88.7% 标称；改成 5% 后降到 65.2%。

> 附带结论：**切换活动电源方案不需要管理员权限**。`powercfg /setactive` 底层是
> `PowerSetActiveScheme(NULL, ...)`，改的是当前用户的活动方案（HKCU），不是 HKLM 里的方案内容。
> 已用非管理员 shell（`elevated=False`，UAC 开启）实测：平衡 / 节能 / 卓越性能 来回切换全部成功。
> ScreenDim 直接用原生 `powrprof.dll` 调这个 API。
>
> 反例：`powercfg /setacvalueindex` 改的是**方案内容**，理论上需要提权——但本机实测也成功了，
> 说明当前账户（`XS-202506211503\Administrator`）权限较宽松。

---

## 3. Edge 硬件加速

**问题**：`Local State` 里 `hardware_acceleration_mode.enabled = false`，导致：

* GPU 进程退化成 `--use-angle=d3d11-warp-webgl`（WARP = Windows 的 **CPU 软件光栅化**）
* 视频不走 GPU 硬解（`VideoDecode` 引擎利用率恒为 0）
* Edge 白烧 ~87% CPU

**定位依据**：用一个全新临时配置启动 Edge，其 GPU 进程**没有任何 gl/angle 参数**（走硬件默认），
而你的配置强制 WARP —— 说明**显卡和驱动 617.42 完全正常**，纯粹是配置状态问题。
`edge://flags` 无覆盖项，也没有相关组策略。Crashpad 里有 2 个崩溃报告（最近 2026-10-02），
推测是当时 GPU 崩过之后加速被关掉了。

**改动**：把该值改为 `true`（改前 Edge 必须完全退出，否则会被覆盖回去）。

```powershell
# 备份在这儿（完整原文件）：
# %LOCALAPPDATA%\Microsoft\Edge\User Data\Local State.screendim-backup-20261007-014922

# 回滚：关掉 Edge，把上面那个备份复制回 Local State
```

**图形界面等价操作**（推荐用这个，改完重启 Edge 即可）：
`edge://settings/system` → 打开「使用图形加速(如可用)」，重启浏览器。

**实测**：

| 指标 | 改前 | 改后 |
|---|---|---|
| GPU 进程后端 | WARP（软件） | 硬件默认 |
| Edge CPU | 87.1 % | 29.9 % |
| GPU 功耗 | 56.6 W | 42 W |
| GPU 利用率 | 22.6 % | 4.8 % |

**遗留观察**：验证当时没有视频在播，所以 `VideoDecode` 仍是 0%，无法证明硬解已经启用。
下次你播视频时如果 Edge CPU 明显低于以前，就是硬解生效了。

---

## 还没动的（都是你自己的程序，需要你决定）

12 秒采样下仍占 CPU 的大头：

| 进程 | 说明 |
|---|---|
| `n.e.k.o` + `hyphelper` + `recorder` + `projectneko_server` | Electron AI 桌宠/助手，带屏幕捕获和录制（`recorder.exe` 累计 6810s CPU） |
| `feishu` | 飞书 |
| `msiafterburner` | 微星小飞机（监控软件，本身也吃一点） |

这些要么是你要用的功能，要么涉及你可能在意的会话状态，所以没有擅自处理。

---

## 测量方法备忘

* GPU 功耗：`nvidia-smi --query-gpu=power.draw --format=csv,noheader,nounits`
* 每进程 CPU：`Get-Counter '\Process(*)\% Processor Time'`（注意 PowerShell 变量名大小写不敏感，
  别用 `$n` 当循环变量去覆盖 `$N`）
* 每进程 GPU 引擎：`Get-Counter '\GPU Engine(*)\Utilization Percentage'`，
  实例名形如 `pid_1234_luid_..._engtype_3D` / `..._engtype_VideoDecode`
* 实际频率：`Get-Counter '\Processor Information(*)\% Processor Performance'`（100% = 标称）
* **面板背光功耗主机侧测不到**（`nvidia-smi` 不含它，本机也没有 UPS/电池可读整机功率），
  只能上插座功率计。
