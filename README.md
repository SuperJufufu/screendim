# ScreenDim — turn off the monitor backlight without breaking screen capture

**A Windows daemon that sets the physical monitor backlight to 0 over DDC/CI (VCP 0x10), drops refresh rate to 60 Hz, and leaves the Windows framebuffer, DWM composition and GPU scanout completely untouched** — so screenshots, screen recording, OCR and desktop-vision apps keep seeing the real picture while the screen looks black. Any mouse move or keypress restores brightness and refresh rate instantly.

**Why not DPMS or `powercfg /off`?** Those blank the whole output pipeline, so every capture API returns black frames. This dims the backlight only.

`windows` `ddc-ci` `monitor` `backlight` `screen-capture` `ocr` `power-saving` `high-refresh-rate` `csharp`

---

## 中文说明

### 屏"灭"了，画面还在

空闲后把两块屏的**背光**降到 0（外观上就是全黑）**并把刷新率降到 60Hz**，但 Windows 的帧缓冲、DWM 合成、GPU 扫描输出**一点没动**。
所以截屏、录屏、OCR、桌面识别程序拿到的仍然是完整的原始画面。鼠标一动（或按键/滚轮）自动恢复原亮度和原刷新率。

适用场景：2K 高刷双屏 24 小时开着太费电，但又要保证后台的截图 / 桌面识别程序 7×24 正常工作。

**省电实测（本机 180Hz + 260Hz 双屏）**：

| 措施 | GPU 功耗 | 增量 |
|---|---|---|
| 180/260Hz，背光正常 | 58.5 W | — |
| **双 60Hz**，背光正常 | 39.3 W | **−19.2 W** |
| 双 60Hz + 背光 0 | （背光省下的部分不在 GPU 统计内） | — |

降刷新率这 19.2W 是**叠加在背光熄灭之上**的纯增量，且还不含显示器自身 T-CON/scaler 因降帧省下的功耗。

---

## 1. 为什么它不影响截屏和识别（原理）

键盘/鼠标 → 你的程序，和显示器背光是**两条互不相干的路**：

```
            ┌──────────────── GPU ────────────────┐
 应用窗口 → │ DWM 合成 → 帧缓冲 → 扫描输出 ────────┼──→ 显示器 scaler ──→ 面板
            └────────────────────────────────────┘        ↑
                                                          │  DDC/CI (I²C / AUX)
          ScreenDim ─────────────────────────────────────┘   只改背光
```

* 截屏走的路径（GDI `BitBlt` / `PrintScreen`、DXGI Desktop Duplication、Windows.Graphics.Capture）
  读的是 **DWM 合成后的帧缓冲**，DDC 写的是**显示器内部的背光寄存器**，两者根本不在一条链路上。
* 所以不管你的截图/识别程序用哪种抓屏方式，拿到的像素都完全一样。

**降刷新率呢？** 它确实会动 GPU 扫描状态，但**只动时序，不动分辨率和色深**，所以抓到的帧几何完全不变。
实测（ffmpeg `ddagrab`，即 DXGI Desktop Duplication）：

| 状态 | 抓屏分辨率 | 单帧 PNG 大小 |
|---|---|---|
| 180/260Hz，背光正常 | 2560×1440 | 3526 KB |
| 60/60Hz，背光正常 | 2560×1440 | 3529 KB |
| 60/60Hz，**背光 0** | 2560×1440 | 3524 KB |

三种状态分辨率相同、帧大小差异 <0.2%。GDI 抓屏同样验证通过（5120×1440 完整虚拟桌面）。

**这不是**那种用 gamma ramp（`SetDeviceGammaRamp`）把画面调黑的做法 —— 那种会改 GPU 扫描输出状态，
不同抓屏方式表现不一致（有的能抓到，有的抓到全黑），不可靠，本项目不使用。

### 为什么不直接用 Windows 自带的"关闭显示器"
系统电源计划的"关闭显示器"会**真的关掉 GPU 输出**：显示链路断开、分辨率变化、窗口重排、唤醒要 1～3 秒，
抓屏直接黑屏或拿到错误的桌面尺寸 —— 识别程序必然出问题。
ScreenDim 反过来：用 `SetThreadExecutionState(ES_DISPLAY_REQUIRED)` **阻止** Windows 关屏/休眠/进屏保/自动锁屏，
再由我们自己通过 DDC 把背光熄掉。系统始终认为显示器是"开着"的。

> 顺带解决：屏保和自动锁屏也会让抓屏拿到屏保/锁屏画面，本程序一并屏蔽。

---

## 2. 实测结果（本机，RTX 5070 Ti + HKC G27H2 + Philips 27M4N5500PT，5120×1440）

**DDC/CI 能力**（`--list`）

| 显示器 | VCP 0x10 亮度 | VCP 0xD6 电源 |
|---|---|---|
| HKC G27H2 (0,0) | min 0 / 当前 70 / max 100 | 当前 1 |
| Philips 27M4N5500PT (2560,0) | min 0 / 当前 90 / max 100 | 当前 1 / max 5 |

**灭屏期间抓屏测试**（`--selftest`，"见证像素"法，三次运行）

```
run 1:
captured-while-dark: meanLuma=41.3  stdLuma=52.1  nonBlackPixels=91.1%
witness pixels (static across the lit control interval): 5740159 = 77.9% of screen
  of those, changed when the panels went dark          :  153071 = 2.667%
animated share, lit pair (control)  : 22.144%
animated share, dark pair           :  3.440%

run 2:
captured-while-dark: meanLuma=80.1  stdLuma=90.2  nonBlackPixels=90.5%
witness pixels (static across the lit control interval): 6999417 = 94.9% of screen
  of those, changed when the panels went dark          :   31990 = 0.457%
animated share, lit pair (control)  :  5.064%
animated share, dark pair           :  5.162%

run 3 (final build):
captured-while-dark: meanLuma=79.5  stdLuma=86.6  nonBlackPixels=90.9%
witness pixels (static across the lit control interval): 7106020 = 96.4% of screen
  of those, changed when the panels went dark          :   85674 = 1.206%
animated share, lit pair (control)  :  3.618%
animated share, dark pair           :  3.631%

run 4 (after hardening):
captured-while-dark: meanLuma=40.8  stdLuma=50.9  nonBlackPixels=91.2%
witness pixels (static across the lit control interval): 7279889 = 98.7% of screen
  of those, changed when the panels went dark          :   11992 = 0.165%
animated share, lit pair (control)  :  1.260%
animated share, dark pair           :  0.667%

run 5 (final build, with refresh-rate reduction):
witness pixels (static across the lit control interval): 7284024 = 98.8% of screen
  of those, changed when the panels went dark          :   24384 = 0.335%
```

解读：先在**点亮状态**下连拍两帧，找出完全静止的像素（数以百万计）；然后灭屏再拍。
如果灭屏干扰了抓屏，这些"见证像素"应该几乎全部变黑（≈100%）。
实测只有 **0.17%～2.7%** 变化 —— 那是动画窗口（视频/进度条/聊天窗口）在继续刷新，不是灭屏造成的。
最有力的对照是 run 3/4：**点亮时的动画比例与灭屏时几乎相同**（3.618% vs 3.631%；run 4 甚至灭屏时更低，1.260% vs 0.667%），
等于同一个噪声底 —— 抓屏对"灭屏"这个动作完全无感。

> 两种情况下自测不会给出假 PASS：静止像素少于全屏 1%（比如正在全屏放视频）时报 **INCONCLUSIVE**；
> 请求的亮度恰好等于当前亮度、根本没发生变暗时也报 **INCONCLUSIVE**，而不是拿"灭屏前后一样"当成功。

灭屏时抓到的画面本身是完整桌面（`非黑像素 90%+`）。对比图见 `%LOCALAPPDATA%\ScreenDim\shots\`。

**响应延迟**（双屏，最终构建）

| 项目 | 实测 |
|---|---|
| 灭屏（DDC 写入 + 回读校验，两屏并行） | ~300 ms |
| 灭屏后降到 60Hz（发生在屏已全黑时，不可见） | ~1.5 s |
| **鼠标一动 → 屏幕重新亮起** | **~470 ms** |
| 刷新率恢复完成（之后有一次重同步闪屏） | ~1.44 s |
| 灭屏后保持黑暗 | 实测连续 61.5 秒不自行恢复，直到真实输入 |

**唤醒顺序为什么是"先亮背光、再改刷新率"？** 实测发现：`ChangeDisplaySettingsEx` 返回后约 70ms
DDC 通道就恢复，但**紧接着做 DDC 写入会被阻塞重试**——先改刷新率再写背光时，那 2.4 秒全花在等待链路上，
用户要对着黑屏等 3.4 秒。改成先亮背光（链路稳定，0.47 秒），再改刷新率：

| 唤醒顺序 | 屏幕可见 | 刷新率恢复 | 观感 |
|---|---|---|---|
| 先改刷新率 | 3402 ms | 同刻 | 长时间黑屏，无闪屏 |
| **先亮背光（当前）** | **467 ms** | 1437 ms | 立刻可见，之后一次重同步闪屏 |

灭屏方向反过来：先熄背光、再降刷新率，因为那时没人在看，模式切换完全不可见。
不想要那次闪屏就加 `--no-hz`（唤醒延迟 454ms，但不省那 19.2W）。

**自动循环**（`--idle 8 --hz 60`，真实 DDC，最终构建）

```
14:41:04.468  dim ok: \\.\DISPLAY1 brightness=0
14:41:04.531  dim ok: \\.\DISPLAY2 brightness=0
14:41:04.858  hz: \\.\DISPLAY1 180 -> 60Hz (saved 180Hz)
14:41:06.141  hz: \\.\DISPLAY2 260 -> 60Hz (saved 260Hz)
14:41:06.150  state=DARK monitors=2/2 level=0 mode=dim
14:41:26.315  heartbeat idle=21s sysIdle=720s dark=True
14:41:56.388  heartbeat idle=51s sysIdle=750s dark=True
14:42:16.218  max-seconds reached, exiting
14:42:16.681  restore ok: \\.\DISPLAY1=70
14:42:16.681  restore ok: \\.\DISPLAY2=90
14:42:18.170  hz restored: \\.\DISPLAY1 -> ok
14:42:18.171  hz restored: \\.\DISPLAY2 -> ok
14:42:18.172  state=AWAKE restore=complete hz=restored
```

空闲 8 秒自动灭屏并降到 60Hz → **面板连续保持黑暗 71 秒** → 退出时先恢复背光、再恢复刷新率，
两个恢复记录都被清理。另一次定向实测：灭屏后故意不触发任何输入，面板连续 9.7 秒保持黑暗。

（日志里的 `idle=51s` 是钩子判定的"无有效输入"，`sysIdle=750s` 是系统计时器 —— 正是抗抖动设计生效，
抖动不再阻止灭屏。）

**灭屏状态下 `--stop`**：面板 0/0 + 60/60Hz、两个记录都在 → 退出后恢复 70/90 + 180/260Hz、记录清空、干净退出。

**退出安全性**：灭屏状态下 `--stop` / `--max-seconds` / Ctrl+C / 进程退出都会等待恢复完成
（最多 15 秒）再退出，退出码非 0 表示恢复未完成。实测：面板 0/0 且有恢复记录时执行 `--stop`，
两屏恢复 70/90、记录清空、干净退出。即使被强杀，`state.txt`（原子写入）也会在下次启动时自动把亮度写回。

**自测/恢复的前置检查**：`--selftest` 在守护进程正在运行时、或存在未处理的恢复记录时会拒绝执行（退出码 2），
避免把"已经变暗的面板"误当成原始亮度、覆盖并删除真正的恢复记录；`--restore` 会持有单实例锁跑完整个过程，
不会把刚启动的守护进程写的记录删掉。

---

## 3. 使用

```powershell
cd C:\Users\Administrator\Desktop\ScreenDim
.\install.ps1          # 开机自启（启动文件夹快捷方式，不需要管理员权限）+ 立即启动
```

常用命令：

```powershell
.\ScreenDimCLI.exe --status        # 看当前是否处于灭屏状态
.\ScreenDimCLI.exe --list          # 看两块屏的 DDC 能力与当前亮度
.\ScreenDimCLI.exe --hz-list       # 看每块屏在当前分辨率下支持哪些刷新率
.\ScreenDimCLI.exe --plan-list     # 看所有电源方案和当前活动方案
.\ScreenDimCLI.exe --hz-test       # 降到 60Hz、验证抓屏、再回滚原刷新率
.\ScreenDimCLI.exe --selftest      # 复现"灭屏不影响抓屏"的验证（会短暂灭屏约 8 秒）
.\ScreenDimCLI.exe --dim-now       # 立刻进省电（灭屏 + 60Hz + 切节能方案）
.\ScreenDimCLI.exe --wake-now      # 立刻恢复（背光 + 刷新率 + 卓越性能）
.\ScreenDimCLI.exe --restore       # = --restore-saved，从崩溃记录里恢复全部三项
.\ScreenDimCLI.exe --stop          # 停止守护进程（退出前自动恢复）
.\uninstall.ps1                    # 停止 + 恢复 + 删除自启
```

默认行为：**空闲 60 秒**后把两块屏背光降到 0、降到 60Hz、并切换到配置的省电方案；
鼠标移动超过 4px、点击、滚轮 → 立刻恢复原亮度、原刷新率、原电源方案。

### 网页控制面板「省电专家」

桌面上的 **省电专家** 快捷方式，双击即可（会自动确保守护进程在跑，然后打开
`http://127.0.0.1:8765/`）。面板显示：

| 区域 | 内容 |
|---|---|
| 屏幕 | 当前是「正常」还是「省电中」，以及背光 / 刷新率 |
| CPU 占用 | 来自 `GetSystemTimes` 差值，不用 PerformanceCounter |
| GPU 占用 | 来自 **NVML**（`nvml.dll`，驱动自带），不是 spawn `nvidia-smi` |
| 内存 | 已用 / 总量（GB） |
| GPU 功耗 | 瓦特，同样来自 NVML |
| 电源方案 / 空闲秒数 | 实时 |

三个按钮：

* **省电模式**（红=关 / 绿=开）：点一下立刻黑屏 + 60Hz + 节能方案 + 降频；再点一下恢复。
  开启期间**动鼠标会短暂亮屏 15 秒**（`--peek` 可调），方便你回到页面把它关掉；15 秒没动静会自动重新变黑。
  把 `--peek` 设成 `0` 就变成完全锁死（鼠标不再唤醒，只能靠键盘解锁）。
* **5 分钟自动省电**（绿=开 / 红=关）：控制"闲置 `--idle` 秒后自动进省电"。关闭后自动行为停止，
  但另外两个按钮仍然可用。
* **锁定省电**（绿=锁定中）：**完整锁死**的省电模式 —— 黑屏 + 降到 60Hz + 节能方案，
  而且**鼠标和键盘都完全不会取消**，只有再点这个按钮才恢复。适合"人离开一会儿"。
  锁定期间每 5 秒重校验一次背光，防止某块屏漏写。
  恢复方式：点按钮，或在命令行跑 `ScreenDimCLI.exe --wake-now` / `--stop`（不依赖看得见屏幕）。
* **仅降亮度（可选）**：默认关闭。如果你想要"只压背光、不动刷新率"，加 `--lock-brightness-only`
  启动参数即可 —— 但**在你那块 Philips 上无效**，原因见下面的"已知硬件限制"。

#### 已知硬件限制：Philips 27M4N5500PT 的亮度是按刷新率档位存的

实测发现这块屏有一个很坑的行为：**它收到 DDC 亮度后会回读成功，但不真正应用**，
必须等显示链路重新协商（刷新率真的变了）才生效；而且**切回原来的刷新率会把亮度也带回去**。

验证过程：把亮度写 0（回读 0，屏幕仍亮）→ 刷新率 275→60 并停住 → **屏幕变黑**；
改成 275→260→275（切走再切回）→ 又变亮。重复下发相同模式是驱动的 no-op，不触发。

所以对它来说"只降亮度、不动刷新率"在硬件层面做不到。这就是"锁定省电"按钮会连刷新率一起
降到 60Hz 并**停在那里**的原因。只有 HKC 那块屏能纯响应亮度。

顺带：`--no-hz` 在这台机器上会导致**副屏压不暗**，慎用。


技术细节：服务端是**手写的极简 HTTP/1.1**（TcpListener 绑 `127.0.0.1`），没有用 `HttpListener` ——
后者需要 `netsh http add urlacl` 授权，而普通用户绑回环端口什么都不需要。页面资源编译进 exe，不依赖外部文件。

### 图标

桌面快捷方式的电源图标由 `make-icon.ps1` 生成（`power.ico`，16/24/32/48/64/128/256 七个尺寸，
Vista+ 的 PNG 压缩 ICO 格式，深色圆角底 + 绿色电源符号）。想改颜色或形状就编辑那个脚本再跑一次：

```powershell
.\make-icon.ps1      # 重新生成 power.ico 和 docs-icon.png
.\install.ps1        # 重新应用（会把新图标挂到快捷方式上）
```

`install.ps1` 在 `power.ico` 不存在时会自动退回 exe 自带图标，不会报错。

> ⚠️ `ui.html` 和三个 `.ps1` 脚本必须以 **UTF-8 with BOM** 保存。PowerShell 会把无 BOM 的 `.ps1`
> 按系统 ANSI(GBK) 解码，中文变乱码后可能吞掉引号导致解析失败（实测踩过）。`build.ps1` 里 csc
> 的 `/resource:` 参数也需要文件存在。

### 参数

| 参数 | 默认 | 说明 |
|---|---|---|
| `--idle <秒>` | 60 | 空闲多久后进省电 |
| `--level <0-100>` | 0 | 省电时的背光值 |
| `--hz <n>` | 60 | 省电时降到的刷新率；`0` 或不支持时不动 |
| `--no-hz` | — | 完全不动刷新率（唤醒没有闪屏，但少省 ~19W） |
| `--saver-plan <p>` | saver | 省电时切到的电源方案：`saver`(节能) / `balanced`(平衡) / `high` / `ultimate` / `<GUID>` / 名称子串 |
| `--no-plan` | — | 完全不动电源方案 |
| `--peek <sec>` | 15 | 手动省电时光标唤醒后的亮屏秒数；`0` = 完全锁死（鼠标不唤醒，靠键盘解锁） |
| `--lock-level <n>` | 0 | 「仅降亮度」按钮降到的背光值 |
| `--mode dim\|off` | dim | `dim` = 只调背光；`off` = DDC 电源关闭（**有风险**，见下） |
| `--wake mouse\|any` | mouse | `any` = 键盘也唤醒 |
| `--threshold <px>` | 4 | 光标净位移超过多少像素算"动了" |
| `--reassert <秒>` | 20 | 定期重新确认灭屏值，防止显示器自己回弹 |
| `--restore-level <n>` | 70 | 读不到原始亮度时的兜底恢复值 |
| `--allow-sleep` | 关 | 不阻止系统休眠/关屏 |
| `--dry-run` | 关 | 只记录决策，不真的写显示器/电源方案 |
| `--allow-power-off` | 关 | 允许 `--mode off` 真正执行 |

配置文件都在 `%LOCALAPPDATA%\ScreenDim\`：`screendim.log`（日志）、`status.txt`（状态）、
`state.txt`（背光恢复记录）、`state-hz.txt`（刷新率恢复记录）、`state-plan.txt`（电源方案恢复记录）、
`report.txt`（命令输出）、`shots\`（验证截图）。

刷新率的可用值是**按当前分辨率和色深枚举**出来的（`--hz-list`）。如果目标值不被支持，
程序会选不高于目标值的最大可用刷新率；一块屏本来就不高于目标值时不会被改动。
刷新率只在本次会话生效，**不会写进注册表**改变你的默认显示模式。

### 电源方案切换需要管理员权限吗？不需要

`powercfg /setactive` 底层是 `PowerSetActiveScheme(NULL, ...)`，改的是**当前用户**的活动方案
（HKCU 下的 `ActivePowerScheme`），不是 HKLM 里的方案内容。所以**天然不需要提权**。

已在本机实测（非管理员 shell，`elevated=False`，UAC 开启）：平衡 / 节能 / 卓越性能 之间来回切换全部成功。
ScreenDim 直接用原生 `powrprof.dll` 调用这个 API，不 spawn `powercfg.exe`。

需要管理员的是**修改方案内容**（`powercfg /setacvalueindex` 写方案、改电源按钮行为等）——
不过本机实测连这个也成功了，说明当前账户的权限比较宽松。

> ⚠️ 切到 `saver`（节能）会限制 CPU 频率与核心停泊，**后台程序在省电期间也会被限速**。
> 你的截图/桌面识别程序在省电期间仍能工作，但吞吐会下降。想更保守就用 `--saver-plan balanced`。

---

## 4. 风险、边界与回滚

**会不会卡在黑屏？** 不会。多层保护：

1. 恢复前会把"原始亮度"和"原始刷新率"**原子写入** `state.txt` / `state-hz.txt`
   （在真正改动**之前**）；如果写不进去（磁盘满/被占用），程序会**拒绝灭屏**并记日志
   ——宁可不省电，也不丢失用户的原始设置。实测：模拟"灭屏+降帧率时崩溃"后启动，
   两块屏的亮度和刷新率都被正确还原，记录被清理。
2. 恢复记录只在"所有登记过的显示器都成功写回"之后才删除。拔线/关机导致某块屏这次联系不上时，
   它的记录会被保留，下次继续恢复；`--restore` 也会在部分失败时保留记录而不是清掉。
3. 任何退出路径（控制命令 `stop`、`--max-seconds`、Ctrl+C、`AppDomain.ProcessExit`）都会
   **等待恢复完成**（最多 15 秒）再退出，不会把恢复过程截断；恢复未完成时退出码非 0。
4. 面板回读值与请求值不一致时（例如固件把 0 钳制成 10），程序区分"写入已下发"和"校验通过"，
   仍然进入灭屏状态并保留用户的原亮度，不会把原始记录丢掉。
5. 即使某个 DDC 调用卡死超过 4 秒，程序也不会销毁那个还握在 worker 手里的显示器句柄
   （dxva2 句柄值很小且会被复用，销毁后可能写到**别的**显示器上）——宁可泄漏一个句柄到进程退出。
6. 引擎线程若异常退出，主循环会在一个轮询周期内发现并主动退出，让下次启动来恢复，
   而不是假装健康地一直握着"保持唤醒"。
7. 单实例互斥：重复启动的守护进程会立刻退出（退出码 2），避免两个实例互相抢背光/刷新率。
8. 兜底命令：`.\ScreenDimCLI.exe --restore-saved`（`--restore` 等价，不依赖守护进程）。

**`--mode off` 为什么默认禁用？**
`VCP 0xD6 = 4` 是让显示器进入 DDC 电源关闭状态。理论上最省电、最黑，但部分显示器进入该状态后
DDC 通道也会停止响应，只能用机身按键唤醒；个别机型还会让 Windows 认为显示器已断开，导致
分辨率变化、窗口重排 —— 这恰恰会破坏"画面还在"的前提。所以默认只用 `--mode dim`
（背光=0），它不动显示链路，最安全。

**亮度 0 是否等于肉眼全黑？** 取决于面板。多数显示器 VCP 0x10=0 时背光基本熄灭（可能还有极微弱辉光）。
请先用 `--selftest` 看一眼实际观感；如果还不够黑，再考虑谨慎尝试 `--mode off --allow-power-off`。

**唤醒延迟**：屏幕约 **0.47 秒**亮起；刷新率约 1.44 秒后恢复，伴随一次重同步闪屏。鼠标移动超过阈值即触发。
不想要闪屏就加 `--no-hz`（唤醒 0.45 秒，但不省那 19.2W）。

**关于"鼠标抖动"**：程序用 **100ms 内的光标净位移**判断有没有真的动，
而不是逐事件判断。原因：1000Hz 高刷鼠标在快速滑动时**每个事件**的位移可能不到 1px，
而抖动传感器又会持续产生 ±1px 的事件 —— 按单次事件判断两种都会判断错。净位移法两者都正确。

**注意**：本程序不修改系统电源计划。它通过 `ES_DISPLAY_REQUIRED` 阻止 Windows 关屏，
进程退出后系统行为立即恢复原样。

---

## 5. 构建

```powershell
.\build.ps1
```

同一个源文件编译出两个二进制：

* `ScreenDim.exe` —— `winexe`，**永远没有控制台窗口**，守护进程用这个；
* `ScreenDimCLI.exe` —— 控制台版，用于 `--list` / `--selftest` / `--status` 等命令。

依赖：Windows 自带的 .NET Framework 4.x（用 `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` 编译），
无需额外运行时。
