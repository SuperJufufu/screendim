// ScreenDim - keep the Windows framebuffer 100% alive while making the physical
// panels dark through the monitor's DDC/CI backlight channel.
//
// Why this does not affect screen capture / desktop recognition:
//   DDC/CI writes travel over the display data channel (I2C/AUX) straight to the
//   monitor's scaler. The GPU render target, the DWM composition surface and the
//   GPU scanout are never touched, so GDI BitBlt, PrintScreen, DXGI Desktop
//   Duplication and Windows.Graphics.Capture all still read the identical pixels.
//   (This is NOT the same as a gamma-ramp trick, which does modify GPU scanout
//   state and is therefore capture-path dependent.)
//
// The process also holds ES_DISPLAY_REQUIRED / ES_SYSTEM_REQUIRED so Windows
// never powers the displays down itself (that WOULD break capture), never runs a
// screensaver and never auto-locks.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ScreenDim
{
    // ---------------------------------------------------------------- Win32 ---
    internal static class Native
    {
        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;

        public const int SRCCOPY = 0x00CC0020;
        public const int CAPTUREBLT = 0x40000000;

        public const uint ES_CONTINUOUS = 0x80000000u;
        public const uint ES_SYSTEM_REQUIRED = 0x00000001u;
        public const uint ES_DISPLAY_REQUIRED = 0x00000002u;

        public const int WH_MOUSE_LL = 14;
        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_TIMER = 0x0113;
        public const int WM_QUIT = 0x0012;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONDOWN = 0x0204;
        public const int WM_RBUTTONUP = 0x0205;
        public const int WM_MBUTTONDOWN = 0x0207;
        public const int WM_MBUTTONUP = 0x0208;
        public const int WM_MOUSEWHEEL = 0x020A;
        public const int WM_XBUTTONDOWN = 0x020B;
        public const int WM_XBUTTONUP = 0x020C;
        public const int WM_MOUSEHWHEEL = 0x020E;

        public const int VK_LBUTTON = 0x01;
        public const int VK_RBUTTON = 0x02;
        public const int VK_MBUTTON = 0x04;
        public const int VK_XBUTTON1 = 0x05;
        public const int VK_XBUTTON2 = 0x06;

        public const byte VCP_BRIGHTNESS = 0x10;
        public const byte VCP_POWER_MODE = 0xD6;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
        }

        // ---- display mode (refresh rate) ----
        // Layout must match Win32 DEVMODEW exactly: 220 bytes on x64. Both arms of the
        // dmOrientation/dmPosition union are 16 bytes, so declaring the second arm keeps
        // every later field at the right offset.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        public const int ENUM_CURRENT_SETTINGS = -1;
        public const uint DM_BITSPERPEL = 0x00040000;
        public const uint DM_PELSWIDTH = 0x00080000;
        public const uint DM_PELSHEIGHT = 0x00100000;
        public const uint DM_DISPLAYFREQUENCY = 0x00400000;
        public const int DISP_CHANGE_SUCCESSFUL = 0;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DEVMODE lpDevMode,
                                                        IntPtr hwnd, uint dwflags, IntPtr lParam);

        // ---- power plan (scheme) ----
        // PowerSetActiveScheme(NULL, ...) writes the *per-user* active scheme, which is why
        // switching plans needs no elevation - unlike editing a scheme's contents.
        [DllImport("powrprof.dll", SetLastError = true)]
        public static extern uint PowerGetActiveScheme(IntPtr UserRootPowerKey, out IntPtr ActivePolicyGuid);

        [DllImport("powrprof.dll", SetLastError = true)]
        public static extern uint PowerSetActiveScheme(IntPtr UserRootPowerKey, ref Guid SchemeGuid);

        [DllImport("kernel32.dll")]
        public static extern IntPtr LocalFree(IntPtr hMem);

        // ---- system metrics ----
        [StructLayout(LayoutKind.Sequential)]
        public struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        // ---- NVML: read GPU power / utilization straight from the driver (shipped with
        // every NVIDIA install). Far cheaper than spawning nvidia-smi.exe, which matters
        // when the whole point of this program is to not waste power. ----
        [StructLayout(LayoutKind.Sequential)]
        public struct NVML_UTILIZATION { public uint gpu; public uint memory; }

        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
        public static extern int nvmlInit_v2();

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
        public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage")]
        public static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")]
        public static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NVML_UTILIZATION rates);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName", CharSet = CharSet.Ansi)]
        public static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);

        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);
        public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool KillTimer(IntPtr hWnd, UIntPtr uIDEvent);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        /// <summary>
        /// 64-bit monotonic millisecond counter. Environment.TickCount is an Int32 and
        /// wraps every ~24.86 days, which a 24/7 daemon absolutely will hit.
        /// </summary>
        [DllImport("kernel32.dll")]
        public static extern ulong GetTickCount64();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")]
        public static extern IntPtr SetWindowsHookExKb(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint SetThreadExecutionState(uint esFlags);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h,
                                         IntPtr hdcSrc, int xSrc, int ySrc, int dwRop);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        // ---- dxva2 : the officially supported DDC/CI surface ----
        [DllImport("dxva2.dll", SetLastError = true, ExactSpelling = true)]
        public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

        [DllImport("dxva2.dll", SetLastError = true, ExactSpelling = true)]
        public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize,
                                                                 [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true, ExactSpelling = true)]
        public static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize,
                                                          [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true, ExactSpelling = true)]
        public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte bVCPCode,
                                                                  out uint pvct, out uint pdwCurrentValue, out uint pdwMaximumValue);

        [DllImport("dxva2.dll", SetLastError = true, ExactSpelling = true)]
        public static extern bool SetVCPFeature(IntPtr hMonitor, byte bVCPCode, uint dwNewValue);
    }

    // ------------------------------------------------------------ monitor -----
    internal sealed class PhysMon : IDisposable
    {
        public string Device = "?";
        public string Desc = "";
        public IntPtr Handle = IntPtr.Zero;
        public bool ReadOk;
        public uint Cur;
        public uint Max;

        public void Dispose()
        {
            var arr = new Native.PHYSICAL_MONITOR[1];
            arr[0].hPhysicalMonitor = Handle;
            arr[0].szPhysicalMonitorDescription = Desc;
            Native.DestroyPhysicalMonitors(1, arr);
            Handle = IntPtr.Zero;
        }
    }

    internal static class Ddc
    {
        public static List<PhysMon> Enumerate()
        {
            var result = new List<PhysMon>();
            var hmons = new List<IntPtr>();

            Native.MonitorEnumProc cb = delegate(IntPtr hMon, IntPtr hdc, ref Native.RECT r, IntPtr d)
            {
                hmons.Add(hMon);
                return true;
            };

            if (!Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero))
                return result;
            GC.KeepAlive(cb);

            foreach (IntPtr h in hmons)
            {
                var mi = new Native.MONITORINFOEX();
                mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFOEX));
                string dev = "?";
                if (Native.GetMonitorInfo(h, ref mi) && !String.IsNullOrEmpty(mi.szDevice))
                    dev = mi.szDevice;

                uint n;
                if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(h, out n) || n == 0)
                    continue;

                var arr = new Native.PHYSICAL_MONITOR[n];
                if (!Native.GetPhysicalMonitorsFromHMONITOR(h, n, arr))
                    continue;

                for (uint i = 0; i < n; i++)
                {
                    var pm = new PhysMon();
                    pm.Device = dev;
                    pm.Desc = arr[i].szPhysicalMonitorDescription == null ? "" : arr[i].szPhysicalMonitorDescription;
                    pm.Handle = arr[i].hPhysicalMonitor;
                    result.Add(pm);
                }
            }
            return result;
        }

        public static bool ReadVcp(IntPtr h, byte code, out uint cur, out uint max, out uint type)
        {
            cur = 0; max = 0; type = 0;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint t, c, m;
                if (Native.GetVCPFeatureAndVCPFeatureReply(h, code, out t, out c, out m))
                {
                    cur = c; max = m; type = t;
                    return true;
                }
                Thread.Sleep(50);
            }
            return false;
        }

        public static bool WriteVcp(IntPtr h, byte code, uint value, int attempts)
        {
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (Native.SetVCPFeature(h, code, value))
                    return true;
                Thread.Sleep(80);
            }
            return false;
        }

        public static bool WriteVcpVerified(IntPtr h, byte code, uint value, int attempts, out uint readBack)
        {
            bool issued;
            return WriteVcpVerified(h, code, value, attempts, out readBack, out issued);
        }

        /// <summary>
        /// Never ask a panel for more than it reports as its maximum: a recorded value
        /// above the current max can never verify, which would make the retry loops spin
        /// forever and keep the state file alive.
        /// </summary>
        public static uint ClampToMax(IntPtr h, uint want)
        {
            uint cur, max, type;
            if (ReadVcp(h, Native.VCP_BRIGHTNESS, out cur, out max, out type) && max > 0 && want > max)
                return max;
            return want;
        }

        /// <summary>
        /// Writes a VCP value and reads it back.
        /// <paramref name="issued"/> is true if the monitor accepted at least one write,
        /// even when the read-back never matches the requested value (panels clamp, or
        /// have a non-zero minimum brightness). Callers must treat "issued" as "the panel
        /// state changed" - conflating it with "verified" loses the restore target.
        /// </summary>
        public static bool WriteVcpVerified(IntPtr h, byte code, uint value, int attempts,
                                            out uint readBack, out bool issued)
        {
            readBack = UInt32.MaxValue;
            issued = false;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (!Native.SetVCPFeature(h, code, value))
                {
                    Thread.Sleep(90);
                    continue;
                }
                issued = true;
                Thread.Sleep(80);
                uint c, m, t;
                if (ReadVcp(h, code, out c, out m, out t))
                {
                    readBack = c;
                    if (c == value)
                        return true;
                }
                Thread.Sleep(90);
            }
            return false;
        }
    }

    // ---------------------------------------------------------- persistence ---
    internal static class StateStore
    {
        private const string Header = "# ScreenDim state v1";

        /// <summary>Record prefix, so a brightness file and a refresh-rate file are not
        /// silently interchangeable: Load only accepts the kind it is asked for.</summary>
        public const string KindBrightness = "mon";
        public const string KindHz = "hz";

        /// <summary>
        /// Atomically persists the restore targets. Returns false if it could not be
        /// written - callers MUST NOT darken a panel without a durable recovery record,
        /// so on false they must abort the dim rather than proceed.
        /// </summary>
        public static bool Save(string path, Dictionary<string, uint> saved, string kind)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Header);
            foreach (KeyValuePair<string, uint> kv in saved)
                sb.AppendLine(kind + "|" + kv.Key + "|" + kv.Value.ToString(CultureInfo.InvariantCulture));
            // Write-then-rename: a power loss mid-write must never leave a truncated file,
            // because StateStore.Load silently skips unparsable lines and the affected
            // panel would lose its only restore value.
            string tmp = path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    w.Write(sb.ToString());
                    w.Flush();
                    fs.Flush(true);
                }
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return true;
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return false;
            }
        }

        public static Dictionary<string, uint> Load(string path, string kind)
        {
            var d = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(path)) return d;
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    string[] parts = line.Split('|');
                    if (parts.Length != 3 || parts[0] != kind) continue;
                    uint v;
                    if (UInt32.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                        d[parts[1]] = v;
                }
            }
            catch { }
            return d;
        }

        public static void Clear(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    // ---------------------------------------------------------- display mode ---
    // Refresh-rate control. Unlike DDC this DOES touch GPU scanout state, but only the
    // timing: resolution and colour depth are held constant, so the captured frame keeps
    // exactly the same dimensions and content. Measured on this machine: dropping
    // 180Hz+260Hz to 60Hz+60Hz cut GPU power draw by 19.2 W.
    internal static class DisplayMode
    {
        public static Native.DEVMODE Current(string device)
        {
            var dm = new Native.DEVMODE();
            dm.dmSize = (ushort)Marshal.SizeOf(typeof(Native.DEVMODE));
            Native.EnumDisplaySettings(device, Native.ENUM_CURRENT_SETTINGS, ref dm);
            return dm;
        }

        public static bool TryGet(string device, out uint hz, out uint w, out uint h)
        {
            // Enumeration is transiently unreliable while a mode change is being
            // negotiated - observed live: --hz-list reported "could not read the display
            // mode" for a display that was mid-renegotiation. Retry briefly.
            hz = 0; w = 0; h = 0;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                Native.DEVMODE dm = Current(device);
                hz = dm.dmDisplayFrequency; w = dm.dmPelsWidth; h = dm.dmPelsHeight;
                if (w > 0 && h > 0 && hz > 0) return true;
                Thread.Sleep(120);
            }
            return false;
        }

        /// <summary>Distinct refresh rates offered at the display's CURRENT resolution/depth.</summary>
        public static List<uint> Supported(string device)
        {
            var list = new List<uint>();
            Native.DEVMODE cur = Current(device);
            if (cur.dmPelsWidth == 0) return list;
            for (int i = 0; i < 2000; i++)
            {
                var dm = new Native.DEVMODE();
                dm.dmSize = (ushort)Marshal.SizeOf(typeof(Native.DEVMODE));
                if (!Native.EnumDisplaySettings(device, i, ref dm)) break;
                if (dm.dmPelsWidth == cur.dmPelsWidth && dm.dmPelsHeight == cur.dmPelsHeight
                    && dm.dmBitsPerPel == cur.dmBitsPerPel && dm.dmDisplayFrequency > 0
                    && !list.Contains(dm.dmDisplayFrequency))
                    list.Add(dm.dmDisplayFrequency);
            }
            list.Sort();
            return list;
        }

        /// <summary>The best rate at or below <paramref name="want"/> that this panel offers.</summary>
        public static uint PickHz(string device, uint want)
        {
            List<uint> list = Supported(device);
            if (list.Count == 0) return 0;
            if (list.Contains(want)) return want;
            uint best = 0;
            foreach (uint f in list) if (f <= want && f > best) best = f;
            return best > 0 ? best : list[0];
        }

        public static bool TrySetHz(string device, uint hz)
        {
            Native.DEVMODE dm = Current(device);
            if (dm.dmPelsWidth == 0) return false;
            if (dm.dmDisplayFrequency == hz) return true;
            dm.dmDisplayFrequency = hz;
            // Resolution, depth and frequency only. Flags = 0 keeps the change for this
            // session - we never write the user's default mode into the registry.
            dm.dmFields = Native.DM_BITSPERPEL | Native.DM_PELSWIDTH | Native.DM_PELSHEIGHT
                        | Native.DM_DISPLAYFREQUENCY;
            int rc = Native.ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
            return rc == Native.DISP_CHANGE_SUCCESSFUL;
        }

        /// <summary>
        /// Re-issue the display's CURRENT mode (identical values). Some panels - verified on
        /// the Philips 27M4N5500PT - accept a DDC brightness write, report it back, but do
        /// not apply it to the backlight until the link is renegotiated. This is the nudge
        /// that makes the new brightness take effect without changing the resolution or the
        /// refresh rate.
        /// </summary>
        public static bool ReapplyMode(string device)
        {
            Native.DEVMODE dm = Current(device);
            if (dm.dmPelsWidth == 0) return false;
            dm.dmFields = Native.DM_BITSPERPEL | Native.DM_PELSWIDTH | Native.DM_PELSHEIGHT
                        | Native.DM_DISPLAYFREQUENCY;
            int rc = Native.ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
            return rc == Native.DISP_CHANGE_SUCCESSFUL;
        }

        /// <summary>Comma-separated rate list, or "-" when empty.</summary>
        public static string Format(List<uint> v)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < v.Count; i++) { if (i > 0) sb.Append(','); sb.Append(v[i]); }
            return sb.Length == 0 ? "-" : sb.ToString();
        }

        public static List<string> Devices()
        {            var list = new List<string>();
            var hmons = new List<IntPtr>();
            Native.MonitorEnumProc cb = delegate(IntPtr h, IntPtr hdc, ref Native.RECT r, IntPtr d)
            {
                hmons.Add(h);
                return true;
            };
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            foreach (IntPtr h in hmons)
            {
                var mi = new Native.MONITORINFOEX();
                mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFOEX));
                if (Native.GetMonitorInfo(h, ref mi) && !String.IsNullOrEmpty(mi.szDevice)
                    && !list.Contains(mi.szDevice))
                    list.Add(mi.szDevice);
            }
            return list;
        }
    }

    // ----------------------------------------------------------- power plan ---
    // Switching the ACTIVE scheme is a per-user setting (HKCU), so it needs no admin.
    // Verified on this machine from a non-elevated shell: 平衡 / 节能 / 卓越性能 all switch.
    internal static class PowerPlan
    {
        // Well-known Windows schemes; anything else is resolved from powercfg /list.
        public static readonly Guid Balanced = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
        public static readonly Guid HighPerf = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        public static readonly Guid Saver = new Guid("a1841308-3541-4fab-bc81-f71556f20b4a");

        public static Guid? GetActive()
        {
            IntPtr p;
            if (Native.PowerGetActiveScheme(IntPtr.Zero, out p) != 0 || p == IntPtr.Zero) return null;
            try { return (Guid)Marshal.PtrToStructure(p, typeof(Guid)); }
            catch { return null; }
            finally { Native.LocalFree(p); }
        }

        public static bool SetActive(Guid g)
        {
            Guid tmp = g;
            uint rc = Native.PowerSetActiveScheme(IntPtr.Zero, ref tmp);
            if (rc != 0) return false;
            Guid? now = GetActive();
            return now.HasValue && now.Value == g;
        }

        /// <summary>Friendly names of every scheme, as powercfg reports them (localized).</summary>
        public static List<KeyValuePair<Guid, string>> List()
        {
            var result = new List<KeyValuePair<Guid, string>>();
            try
            {
                var psi = new ProcessStartInfo("powercfg.exe", "/list");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    var re = new Regex(@"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*\(([^)]*)\)");
                    foreach (Match m in re.Matches(outp))
                    {
                        Guid g;
                        if (Guid.TryParse(m.Groups[1].Value, out g))
                            result.Add(new KeyValuePair<Guid, string>(g, m.Groups[2].Value.Trim()));
                    }
                }
            }
            catch { }
            return result;
        }

        /// <summary>
        /// Resolve a user-supplied plan spec: a raw GUID, one of the well-known aliases, or a
        /// substring of the scheme's localized name ("平衡", "节能", "卓越性能", ...).
        /// </summary>
        public static bool TryResolve(string spec, out Guid guid, out string resolvedName)
        {
            guid = Guid.Empty; resolvedName = null;
            if (String.IsNullOrEmpty(spec)) return false;

            Guid parsed;
            if (Guid.TryParse(spec, out parsed)) { guid = parsed; resolvedName = spec; return true; }

            string s = spec.Trim().ToLowerInvariant();
            if (s == "balanced" || s == "balance" || s == "b") { guid = Balanced; resolvedName = "Balanced"; return true; }
            if (s == "saver" || s == "powersaver" || s == "power-saver" || s == "min" || s == "max") { guid = Saver; resolvedName = "Power saver"; return true; }
            if (s == "high" || s == "highperformance" || s == "high-performance" || s == "perf") { guid = HighPerf; resolvedName = "High performance"; return true; }

            List<KeyValuePair<Guid, string>> all = List();
            foreach (KeyValuePair<Guid, string> kv in all)
            {
                if (kv.Value.ToLowerInvariant() == s) { guid = kv.Key; resolvedName = kv.Value; return true; }
            }
            foreach (KeyValuePair<Guid, string> kv in all)
            {
                if (kv.Value.ToLowerInvariant().Contains(s)) { guid = kv.Key; resolvedName = kv.Value; return true; }
            }
            if (s == "ultimate")   // localized "卓越性能" is not matched by an English word
            {
                foreach (KeyValuePair<Guid, string> kv in all)
                    if (kv.Value.Contains("卓越") || kv.Value.ToLowerInvariant().Contains("ultimate"))
                    { guid = kv.Key; resolvedName = kv.Value; return true; }
            }
            return false;
        }
    }

    // -------------------------------------------------------- plan store ------
    internal static class PlanStore
    {
        public static bool Save(string path, Guid g)
        {
            string tmp = path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    w.WriteLine("# ScreenDim plan state v1");
                    w.WriteLine("plan|" + g.ToString("D"));
                    w.Flush();
                    fs.Flush(true);
                }
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return true;
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return false;
            }
        }

        public static Guid? Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    string[] parts = line.Split('|');
                    Guid g;
                    if (parts.Length == 2 && parts[0] == "plan" && Guid.TryParse(parts[1], out g)) return g;
                }
            }
            catch { }
            return null;
        }

        public static void Clear(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    // ------------------------------------------------------------- capture ----
    internal static class Capture
    {
        public static Bitmap VirtualDesktop(out string error)
        {
            error = null;
            int x = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
            int y = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            int w = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
            int h = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
            if (w <= 0 || h <= 0) { error = "virtual screen metrics invalid"; return null; }

            IntPtr screen = Native.GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) { error = "GetDC(NULL) failed"; return null; }
            IntPtr mem = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                mem = Native.CreateCompatibleDC(screen);
                bmp = Native.CreateCompatibleBitmap(screen, w, h);
                if (mem == IntPtr.Zero || bmp == IntPtr.Zero) { error = "CreateCompatible* failed"; return null; }
                old = Native.SelectObject(mem, bmp);
                if (!Native.BitBlt(mem, 0, 0, w, h, screen, x, y, Native.SRCCOPY | Native.CAPTUREBLT))
                {
                    error = "BitBlt failed, win32=" + Marshal.GetLastWin32Error();
                    return null;
                }
                Native.SelectObject(mem, old); old = IntPtr.Zero;
                using (Bitmap tmp = Image.FromHbitmap(bmp))
                    return tmp.Clone(new Rectangle(0, 0, w, h), PixelFormat.Format32bppArgb);
            }
            catch (Exception ex) { error = ex.Message; return null; }
            finally
            {
                if (old != IntPtr.Zero) Native.SelectObject(mem, old);
                if (bmp != IntPtr.Zero) Native.DeleteObject(bmp);
                if (mem != IntPtr.Zero) Native.DeleteDC(mem);
                Native.ReleaseDC(IntPtr.Zero, screen);
            }
        }

        private static byte[] Bytes(Bitmap b, out int stride, out int w, out int h)
        {
            w = b.Width; h = b.Height;
            var rect = new Rectangle(0, 0, w, h);
            BitmapData d = b.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                stride = d.Stride;
                byte[] buf = new byte[stride * h];
                Marshal.Copy(d.Scan0, buf, 0, buf.Length);
                return buf;
            }
            finally { b.UnlockBits(d); }
        }

        /// <summary>Mean/stddev of luma and the share of clearly non-black pixels.</summary>
        public static void ContentStats(Bitmap b, out double meanLuma, out double stdLuma, out double nonBlackPct)
        {
            int stride, w, h;
            byte[] buf = Bytes(b, out stride, out w, out h);
            double sum = 0, sum2 = 0; long count = 0, nonBlack = 0;
            int step = 4; // sample every 4th pixel, plenty for a statistic
            for (int yy = 0; yy < h; yy += step)
            {
                int row = yy * stride;
                for (int xx = 0; xx < w; xx += step)
                {
                    int o = row + xx * 4;
                    double luma = 0.114 * buf[o] + 0.587 * buf[o + 1] + 0.299 * buf[o + 2];
                    sum += luma; sum2 += luma * luma; count++;
                    if (luma > 16.0) nonBlack++;
                }
            }
            meanLuma = count == 0 ? 0 : sum / count;
            stdLuma = count == 0 ? 0 : Math.Sqrt(Math.Max(0, sum2 / count - meanLuma * meanLuma));
            nonBlackPct = count == 0 ? 0 : 100.0 * nonBlack / count;
        }

        /// <summary>
        /// Counts pixels that were perfectly static across a lit control interval and
        /// then changed the moment the panels went dark. If dimming leaked into the
        /// captured framebuffer at all, essentially all of these witnesses would change.
        /// </summary>
        public static void StaticPixelsChanged(Bitmap lit1, Bitmap lit2, Bitmap dark1, int thr,
                                               out long staticCount, out long changedAcross)
        {
            staticCount = 0; changedAcross = 0;
            if (lit1.Width != lit2.Width || lit1.Width != dark1.Width ||
                lit1.Height != lit2.Height || lit1.Height != dark1.Height) return;

            int s1, w1, h1, s2, w2, h2, s3, w3, h3;
            byte[] b1 = Bytes(lit1, out s1, out w1, out h1);
            byte[] b2 = Bytes(lit2, out s2, out w2, out h2);
            byte[] b3 = Bytes(dark1, out s3, out w3, out h3);
            int limit = thr * 3;

            for (int y = 0; y < h1; y++)
            {
                int r1 = y * s1, r2 = y * s2, r3 = y * s3;
                for (int x = 0; x < w1; x++)
                {
                    int o1 = r1 + x * 4, o2 = r2 + x * 4, o3 = r3 + x * 4;
                    int dLit = Math.Abs(b1[o1] - b2[o2]) + Math.Abs(b1[o1 + 1] - b2[o2 + 1]) + Math.Abs(b1[o1 + 2] - b2[o2 + 2]);
                    if (dLit > limit) continue;          // this pixel was moving while lit
                    staticCount++;
                    int dDim = Math.Abs(b2[o2] - b3[o3]) + Math.Abs(b2[o2 + 1] - b3[o3 + 1]) + Math.Abs(b2[o2 + 2] - b3[o3 + 2]);
                    if (dDim > limit) changedAcross++;
                }
            }
        }

        /// <summary>Pixel difference over an optional sub-region.</summary>
        public static void Diff(Bitmap a, Bitmap b, Rectangle? region,
                                out double meanAbs, out double pctChanged, out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            meanAbs = 0; pctChanged = 0;
            if (a.Width != b.Width || a.Height != b.Height) return;

            Rectangle r = region.HasValue ? region.Value : new Rectangle(0, 0, a.Width, a.Height);
            r.Intersect(new Rectangle(0, 0, a.Width, a.Height));
            if (r.Width <= 0 || r.Height <= 0) return;

            int sa, sb, wa, ha, wb, hb;
            byte[] ba = Bytes(a, out sa, out wa, out ha);
            byte[] bb = Bytes(b, out sb, out wb, out hb);

            long total = 0, changed = 0;
            long sumAbs = 0;
            int minX = Int32.MaxValue, minY = Int32.MaxValue, maxX = Int32.MinValue, maxY = Int32.MinValue;
            const int threshold = 8;

            for (int yy = r.Top; yy < r.Bottom; yy++)
            {
                int ra = yy * sa, rb = yy * sb;
                for (int xx = r.Left; xx < r.Right; xx++)
                {
                    int oa = ra + xx * 4, ob = rb + xx * 4;
                    int d = Math.Abs(ba[oa] - bb[ob]) + Math.Abs(ba[oa + 1] - bb[ob + 1]) + Math.Abs(ba[oa + 2] - bb[ob + 2]);
                    sumAbs += d;
                    total++;
                    if (d > threshold * 3)
                    {
                        changed++;
                        if (xx < minX) minX = xx;
                        if (yy < minY) minY = yy;
                        if (xx > maxX) maxX = xx;
                        if (yy > maxY) maxY = yy;
                    }
                }
            }
            meanAbs = total == 0 ? 0 : (double)sumAbs / (total * 3.0);
            pctChanged = total == 0 ? 0 : 100.0 * changed / total;
            if (maxX >= minX && maxY >= minY)
                bounds = new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }
    }

    // ---------------------------------------------------------------- log -----
    internal sealed class Log
    {
        private readonly string path;
        private readonly bool verbose;
        private readonly object gate = new object();

        public Log(string path, bool verbose) { this.path = path; this.verbose = verbose; }

        public void W(string msg)
        {
            if (path == null) return;
            lock (gate)
            {
                try
                {
                    if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                        File.Delete(path);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.AppendAllText(path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + msg + Environment.NewLine,
                        Encoding.UTF8);
                }
                catch { }
            }
        }

        public void V(string msg) { if (verbose) W(msg); }
    }

    // ------------------------------------------------------------ options -----
    internal sealed class Options
    {
        public int IdleSec = 60;
        public uint Level = 0;
        public uint Hz = 60;          // refresh rate while dark (0 = leave it alone)
        public string SaverPlan = "balanced";   // power plan while dark (empty/--no-plan = off)
        public Guid SaverPlanGuid = Guid.Empty; // resolved at startup
        public string Mode = "dim";
        public string Wake = "mouse";
        public int ThresholdPx = 4;
        public int ReassertSec = 20;
        public bool AllowSleep = false;
        public uint RestoreFallback = 70;
        public int TickMs = 100;
        public bool NoHook = false;
        public bool Verbose = false;
        public bool DryRun = false;
        public bool AllowPowerOff = false;
        public int MaxSeconds = 0;
        public int SelftestSeconds = 4;
        public int Port = 8765;          // web UI port (0 = disabled)
        public int PeekSec = 15;         // manual mode: seconds lit after input
        public uint LockLevel = 0;       // "brightness only" lock level
        public bool OpenUi = false;      // --open-ui: also open the browser
        public bool NoModeNudge = false; // --no-mode-nudge: skip the display-mode re-poke
        /// <summary>Lock mode: dim BYTE-only (do not change the refresh rate). Off by default
        /// because panels such as the Philips 27M4N5500PT store brightness per timing - they
        /// only take a new level when the mode actually changes.</summary>
        public bool LockBrightnessOnly = false;
        public string ShotDir;

        public string RootDir;
        public string LogPath;
        public string StatePath;
        public string StateHzPath;
        public string StatePlanPath;
        public string CtrlPath;
        public string StatusPath;
        public string ReportPath;

        public string Command = "help";
        public bool UnknownOption = false;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            o.RootDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenDim");
            o.LogPath = Path.Combine(o.RootDir, "screendim.log");
            o.StatePath = Path.Combine(o.RootDir, "state.txt");
            o.StateHzPath = Path.Combine(o.RootDir, "state-hz.txt");
            o.StatePlanPath = Path.Combine(o.RootDir, "state-plan.txt");
            o.CtrlPath = Path.Combine(o.RootDir, "ctrl.txt");
            o.StatusPath = Path.Combine(o.RootDir, "status.txt");
            o.ReportPath = Path.Combine(o.RootDir, "report.txt");
            o.ShotDir = Path.Combine(o.RootDir, "shots");

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string key = a;
                string val = null;
                int eq = a.IndexOf('=');
                if (a.StartsWith("--") && eq > 0) { key = a.Substring(0, eq); val = a.Substring(eq + 1); }

                switch (key)
                {
                    case "--idle": o.IdleSec = (int)Num(val, args, ref i, 5, 86400, o.IdleSec); break;
                    case "--level": o.Level = (uint)Num(val, args, ref i, 0, 100, o.Level); break;
                    case "--hz": o.Hz = (uint)Num(val, args, ref i, 0, 1000, o.Hz); break;
                    case "--no-hz": o.Hz = 0; break;
                    case "--saver-plan": o.SaverPlan = Str(val, args, ref i, o.SaverPlan); break;
                    case "--no-plan": o.SaverPlan = ""; break;
                    case "--mode": o.Mode = Str(val, args, ref i, o.Mode).ToLowerInvariant(); break;
                    case "--wake": o.Wake = Str(val, args, ref i, o.Wake).ToLowerInvariant(); break;
                    case "--threshold": o.ThresholdPx = (int)Num(val, args, ref i, 0, 2000, o.ThresholdPx); break;
                    case "--reassert": o.ReassertSec = (int)Num(val, args, ref i, 0, 86400, o.ReassertSec); break;
                    case "--restore-level": o.RestoreFallback = (uint)Num(val, args, ref i, 0, 100, o.RestoreFallback); break;
                    case "--tick": o.TickMs = (int)Num(val, args, ref i, 20, 1000, o.TickMs); break;
                    case "--port": o.Port = (int)Num(val, args, ref i, 0, 65535, o.Port); break;
                    case "--peek": o.PeekSec = (int)Num(val, args, ref i, 0, 3600, o.PeekSec); break;
                    case "--lock-level": o.LockLevel = (uint)Num(val, args, ref i, 0, 100, o.LockLevel); break;
                    case "--open-ui": o.OpenUi = true; break;
                    case "--no-mode-nudge": o.NoModeNudge = true; break;
                    case "--lock-brightness-only": o.LockBrightnessOnly = true; break;
                    case "--max-seconds": o.MaxSeconds = (int)Num(val, args, ref i, 0, 86400 * 7, o.MaxSeconds); break;
                    case "--selftest-seconds": o.SelftestSeconds = (int)Num(val, args, ref i, 1, 60, o.SelftestSeconds); break;
                    case "--log": o.LogPath = Str(val, args, ref i, o.LogPath); break;
                    case "--state": o.StatePath = Str(val, args, ref i, o.StatePath); break;
                    case "--ctrl": o.CtrlPath = Str(val, args, ref i, o.CtrlPath); break;
                    case "--status-file": o.StatusPath = Str(val, args, ref i, o.StatusPath); break;
                    case "--shots": o.ShotDir = Str(val, args, ref i, o.ShotDir); break;

                    case "--allow-sleep": o.AllowSleep = true; break;
                    case "--no-hook": o.NoHook = true; break;
                    case "--verbose": case "-v": o.Verbose = true; break;
                    case "--dry-run": o.DryRun = true; break;
                    case "--allow-power-off": o.AllowPowerOff = true; break;

                    case "--daemon": case "--watch": o.Command = "daemon"; break;
                    case "--list": o.Command = "list"; break;
                    case "--hz-list": o.Command = "hz-list"; break;
                    case "--hz-test": o.Command = "hz-test"; break;
                    case "--plan-list": o.Command = "plan-list"; break;
                    case "--status": o.Command = "status"; break;
                    case "--selftest": o.Command = "selftest"; break;
                    case "--dim-now": o.Command = "dim"; break;
                    case "--wake-now": o.Command = "wake"; break;
                    case "--restore": case "--restore-saved": o.Command = "restore-saved"; break;
                    case "--stop": o.Command = "stop"; break;
                    case "--help": case "-h": o.Command = "help"; break;
                    default:
                        if (key.StartsWith("--"))
                        {
                            Console.Error.WriteLine("unknown option: " + a);
                            o.Command = "help";
                            o.UnknownOption = true;
                        }
                        break;
                }
            }

            // Normalise the enum-ish options instead of silently doing the wrong thing.
            if (o.Mode != "off") o.Mode = "dim";
            if (o.Wake != "any") o.Wake = "mouse";
            // An unknown option must never be masked by a command that follows it
            // (e.g. "--bogus --daemon" must not silently run the daemon).
            if (o.UnknownOption) o.Command = "help";
            return o;
        }

        private static string Str(string inline, string[] args, ref int i, string def)
        {
            if (inline != null) return inline;
            // Never swallow the next option as a value: "--mode --verbose" must not
            // silently set Mode="--verbose" and drop --verbose.
            if (i + 1 < args.Length && !args[i + 1].StartsWith("-")) return args[++i];
            return def;
        }

        private static long Num(string inline, string[] args, ref int i, long min, long max, long def)
        {
            string s = Str(inline, args, ref i, null);
            long v;
            if (s == null || !Int64.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                return def;
            if (v < min) v = min;
            if (v > max) v = max;
            return v;
        }
    }

    // ------------------------------------------------------- input watcher ----
    // Signal sources, deliberately chosen for robustness:
    //   * pointer movement = NET cursor displacement measured by polling. Per-event
    //     hook deltas are unusable: a 1000 Hz gaming mouse reports sub-pixel deltas
    //     during a fast sweep, while a jittery sensor reports constant tiny deltas.
    //     Net displacement over a ~100 ms tick ignores jitter and still catches
    //     every real movement.
    //   * buttons / wheel  = real low-level hook events. GetAsyncKeyState's low bit
    //     is a "pressed since the previous call" latch, so it fires on stale clicks
    //     the instant the panels go dark (observed in testing).
    //   * keyboard         = its own low-level hook, so "idle" really means idle.
    internal sealed class InputWatcher : IDisposable
    {
        private static Native.LowLevelMouseProc mouseProc;     // must stay rooted
        private static Native.LowLevelKeyboardProc keyProc;

        private IntPtr mouseHook = IntPtr.Zero;
        private IntPtr keyHook = IntPtr.Zero;
        private readonly bool hookWanted;

        private long lastMoveTick;
        private long lastButtonTick;
        private long lastWheelTick;
        private long lastKeyTick;

        public bool MouseHookInstalled { get { return mouseHook != IntPtr.Zero; } }
        public bool KeyHookInstalled { get { return keyHook != IntPtr.Zero; } }

        /// <summary>Monotonic milliseconds; never wraps in practice.</summary>
        public static long NowMs() { return (long)Native.GetTickCount64(); }

        public InputWatcher(bool installHooks)
        {
            long t0 = NowMs();
            lastMoveTick = t0; lastButtonTick = t0; lastWheelTick = t0; lastKeyTick = t0;
            hookWanted = installHooks;
            if (!installHooks) return;
            mouseProc = new Native.LowLevelMouseProc(MouseCallback);
            keyProc = new Native.LowLevelKeyboardProc(KeyCallback);
            Reinstall();
        }

        /// <summary>
        /// (Re-)arm both low-level hooks. Must be called from the thread that pumps
        /// messages (the hooks are dispatched on it). Safe to call repeatedly.
        /// </summary>
        public void Reinstall()
        {
            if (!hookWanted) return;
            if (mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
            if (keyHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(keyHook); keyHook = IntPtr.Zero; }
            try { mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, Native.GetModuleHandle(null), 0); }
            catch { }
            try { keyHook = Native.SetWindowsHookExKb(Native.WH_KEYBOARD_LL, keyProc, Native.GetModuleHandle(null), 0); }
            catch { }
            GC.KeepAlive(mouseProc);
            GC.KeepAlive(keyProc);
        }

        private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // Dispatched on the installing thread from inside its message loop: keep it tiny.
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                long now = NowMs();
                if (msg == Native.WM_MOUSEWHEEL || msg == Native.WM_MOUSEHWHEEL)
                    Interlocked.Exchange(ref lastWheelTick, now);
                else if (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN ||
                         msg == Native.WM_MBUTTONDOWN || msg == Native.WM_XBUTTONDOWN)
                    Interlocked.Exchange(ref lastButtonTick, now);
            }
            return Native.CallNextHookEx(mouseHook, nCode, wParam, lParam);
        }

        private IntPtr KeyCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                if (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN)
                    Interlocked.Exchange(ref lastKeyTick, NowMs());
            }
            return Native.CallNextHookEx(keyHook, nCode, wParam, lParam);
        }

        /// <summary>Called by the poll loop whenever the pointer really moved.</summary>
        public void MarkMoved() { Interlocked.Exchange(ref lastMoveTick, NowMs()); }

        /// <summary>Forget everything that happened before this moment.</summary>
        public void Reset(long tick)
        {
            Interlocked.Exchange(ref lastMoveTick, tick);
            Interlocked.Exchange(ref lastButtonTick, tick);
            Interlocked.Exchange(ref lastWheelTick, tick);
            Interlocked.Exchange(ref lastKeyTick, tick);
        }

        /// <summary>
        /// Wrap-safe STRICT "did t happen strictly after since" test. The strictness is
        /// essential: a freshly Reset() baseline equals darkSince, and treating that as
        /// activity makes the daemon wake itself the instant it goes dark.
        /// </summary>
        private static bool After(long t, long since)
        {
            return unchecked((uint)(t - since) - 1u) < 0x80000000u;
        }

        public bool MouseActivitySince(long since)
        {
            // ButtonsHeld() is deliberately NOT gated by `since`: it is the safety net for
            // the degraded --no-hook mode. In normal mode the hook reports button-down
            // events, and including a merely-HELD button here would make a stuck or
            // long-held button wake the panels the instant they darken (dim/wake thrash).
            return After(Interlocked.Read(ref lastMoveTick), since)
                || After(Interlocked.Read(ref lastButtonTick), since)
                || After(Interlocked.Read(ref lastWheelTick), since)
                || (!MouseHookInstalled && ButtonsHeld());
        }

        public bool KeyActivitySince(long since)
        {
            return After(Interlocked.Read(ref lastKeyTick), since);
        }

        /// <summary>Idle = no significant pointer movement, no click/wheel, no keypress.</summary>
        public long IdleMs()
        {
            long now = NowMs();
            long last = Math.Max(Math.Max(Interlocked.Read(ref lastMoveTick), Interlocked.Read(ref lastButtonTick)),
                                 Math.Max(Interlocked.Read(ref lastWheelTick), Interlocked.Read(ref lastKeyTick)));
            long sig = now - last;
            if (sig < 0) sig = 0;
            if (!KeyHookInstalled)
            {
                // degraded mode: without a keyboard hook, fall back to the system timer
                long sys = SystemIdleMs();
                if (sys < sig) sig = sys;
            }
            return sig;
        }

        public long SystemIdleMs()
        {
            var lii = new Native.LASTINPUTINFO();
            lii.cbSize = (uint)Marshal.SizeOf(typeof(Native.LASTINPUTINFO));
            if (!Native.GetLastInputInfo(ref lii)) return 0;
            return unchecked((uint)Environment.TickCount - lii.dwTime);
        }

        public static Native.POINT Cursor()
        {
            Native.POINT p;
            Native.GetCursorPos(out p);
            return p;
        }

        public static double Distance(Native.POINT a, Native.POINT b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>Physical button held right now (high bit only - no stale latch).</summary>
        public bool ButtonsHeld()
        {
            int[] keys = new int[] {
                Native.VK_LBUTTON, Native.VK_RBUTTON, Native.VK_MBUTTON,
                Native.VK_XBUTTON1, Native.VK_XBUTTON2
            };
            foreach (int k in keys)
                if ((Native.GetAsyncKeyState(k) & 0x8000) != 0) return true;
            return false;
        }

        public void Dispose()
        {
            if (mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
            if (keyHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(keyHook); keyHook = IntPtr.Zero; }
            GC.KeepAlive(mouseProc);
            GC.KeepAlive(keyProc);
        }
    }

    // ------------------------------------------------------------- engine -----
    internal sealed class Engine
    {
        private readonly Options opt;
        private readonly Log log;
        private readonly object gate = new object();
        private readonly Dictionary<string, uint> saved = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, uint> pendingRestore = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, uint> savedHz = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, uint> pendingHz = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        private volatile bool wantDark;
        private volatile bool running = true;
        private Thread worker;

        public volatile bool IsDark;
        public volatile bool ManualMode;      // web UI big button: latched power-save
        public volatile bool AutoEnabled = true;  // web UI toggle: idle-triggered power-save
        /// <summary>Web UI "brightness only" lock: dim the backlight and IGNORE mouse
        /// movement entirely. Only a keypress clears it (the blind escape hatch).</summary>
        public volatile bool BrightnessLock;
        public int PeekSec = 15;              // in manual mode, light up this long on input
        public DateTime LastDarkUtc = DateTime.MinValue;
        public DateTime LastWakeUtc = DateTime.MinValue;
        public int DimRound, RestoreFailures;

        public Engine(Options opt, Log log) { this.opt = opt; this.log = log; }

        public void Start()
        {
            // Hold the display and the system awake: without this Windows would
            // power the panels down itself, which really does stop capture.
            if (!opt.AllowSleep)
            {
                Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED | Native.ES_DISPLAY_REQUIRED);
                log.W("execution state held: display+system required (no monitor power-off, no screensaver, no auto-lock)");
            }
            worker = new Thread(Loop);
            worker.IsBackground = true;
            worker.Name = "ddc-engine";
            worker.Start();
        }

        public void RequestDark() { RequestDark(false); }
        /// <param name="brightnessOnly">true = dim the backlight only; leave the refresh
        /// rate and the power scheme untouched (the "only lower brightness" mode).</param>
        public void RequestDark(bool brightnessOnly) { wantBrightnessOnly = brightnessOnly; wantDark = true; }
        public void RequestAwake() { wantDark = false; }
        public void Stop() { running = false; }

        private volatile bool wantBrightnessOnly;
        /// <summary>True when the current dark state came from the brightness-only mode.</summary>
        public bool IsDimBrightnessOnly { get { return wantBrightnessOnly; } }

        /// <summary>
        /// Runs one work item per physical monitor concurrently. A single DDC/CI
        /// transaction costs ~0.3-0.5 s and each panel sits on its own display data
        /// channel, so this roughly halves the wake-up latency on a dual-head setup.
        /// Returns the number of workers that did NOT finish in time: a straggler is
        /// still holding its physical-monitor handle, and dxva2 handle values are small
        /// and recycled, so the caller must NOT destroy those handles (see callers).
        /// </summary>
        private int ParallelMonitors(List<PhysMon> mons, Action<int> body)
        {
            int n = mons.Count;
            if (n <= 1) { for (int i = 0; i < n; i++) body(i); return 0; }

            int next = -1;
            Thread[] threads = new Thread[n];
            for (int t = 0; t < n; t++)
            {
                threads[t] = new Thread(delegate()
                {
                    while (true)
                    {
                        int j = Interlocked.Increment(ref next);
                        if (j >= n) break;
                        try { body(j); }
                        catch (Exception ex) { log.V("monitor worker error: " + ex.Message); }
                    }
                });
                threads[t].IsBackground = true;
                threads[t].Name = "ddc-mon-" + t;
                threads[t].Start();
            }
            int unfinished = 0;
            for (int t = 0; t < n; t++)
                if (!threads[t].Join(4000))
                {
                    unfinished++;
                    log.W("monitor worker " + t + " did not finish within 4s");
                }
            return unfinished;
        }

        private void Loop()
        {
            long lastStateRefresh = 0;
            while (running)
            {
                try
                {
                    if (!opt.AllowSleep && InputWatcher.NowMs() - lastStateRefresh > 30000)
                    {
                        Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED | Native.ES_DISPLAY_REQUIRED);
                        lastStateRefresh = InputWatcher.NowMs();
                    }

                    if (wantDark && !IsDark) { DoDark(); }
                    else if (!wantDark && IsDark) { DoWake(); }

                    if (IsDark && opt.ReassertSec > 0 && (DateTime.UtcNow - LastDarkUtc).TotalSeconds > 0 && NeedReassert())
                        DoReassert();

                    // Retry a failed restore, but not on every 60 ms iteration: if a
                    // panel stays unplugged for hours this must not spin the DDC bus.
                    if (!IsDark && pendingRestore.Count > 0
                        && (DateTime.UtcNow - lastPendingRetryUtc).TotalSeconds >= 5)
                    {
                        lastPendingRetryUtc = DateTime.UtcNow;
                        RetryPendingRestore();
                    }
                }
                catch (Exception ex)
                {
                    log.W("engine error: " + ex.GetType().Name + ": " + ex.Message);
                }
                Thread.Sleep(60);
            }
            // Final write-back. Must happen under `gate` with the same error handling as
            // every other mutation: if it throws here the engine thread would die while
            // the main loop keeps running, leaving the panels dark with every command
            // (including --wake-now and --stop) silently ineffective.
            if (IsDark)
            {
                try { lock (gate) { TryRestoreAll(true); RestoreHzAll(); RestorePlan(); } }
                catch (Exception ex) { log.W("final restore failed: " + ex.Message); }
            }
        }

        public bool Alive { get { Thread w = worker; return w != null && w.IsAlive; } }

        /// <summary>Seconds since the last real input (polled pointer + hook buttons/keys).</summary>
        private InputWatcher watcherRef;
        public void BindWatcher(InputWatcher w) { watcherRef = w; }
        public long IdleSeconds()
        {
            InputWatcher w = watcherRef;
            return w == null ? 0 : w.IdleMs() / 1000;
        }

        /// <summary>Publish the current status now (so --status is never stale-on-arrival).</summary>
        public void PublishStatus()
        {
            lock (gate) { WriteStatus(); }
        }

        private DateTime lastReassertUtc = DateTime.MinValue;
        private DateTime lastPendingRetryUtc = DateTime.MinValue;
        private bool NeedReassert()
        {
            int sec = opt.ReassertSec;
            // While latched, re-assert much more often: it is the only way a panel that
            // silently ignored a write gets corrected, and there is no user interaction
            // to trigger anything else.
            if (BrightnessLock) sec = Math.Min(sec, 5);
            if (sec <= 0) return false;
            if ((DateTime.UtcNow - lastReassertUtc).TotalSeconds < sec) return false;
            lastReassertUtc = DateTime.UtcNow;
            return true;
        }

        // ---- darken ----
        private void DoDark()
        {
            lock (gate)
            {
                List<PhysMon> mons = Ddc.Enumerate();
                if (mons.Count == 0)
                {
                    log.W("dim skipped: no physical monitors reported DDC handles");
                    foreach (PhysMon m in mons) m.Dispose();
                    return;
                }
                int unfinished = 0;
                try
                {
                    // Entries that were already outstanding before this round (a panel from
                    // an earlier partial restore) must survive even if this round writes
                    // nothing at all.
                    var savedBefore = new Dictionary<string, uint>(saved, StringComparer.OrdinalIgnoreCase);

                    // "brightness only" mode: use its own level and skip the mode changes below
                    bool brightnessOnlyMode = wantBrightnessOnly;
                    var targets = new List<uint>(mons.Count);
                    foreach (PhysMon m in mons)
                    {
                        uint cur, max, type;
                        bool ok = Ddc.ReadVcp(m.Handle, Native.VCP_BRIGHTNESS, out cur, out max, out type);
                        if (ok) { m.ReadOk = true; m.Cur = cur; m.Max = max; }

                        uint target = brightnessOnlyMode ? opt.LockLevel : opt.Level;
                        if (target > max && max > 0) target = max;

                        uint restoreTo;
                        if (saved.ContainsKey(m.Device)) restoreTo = saved[m.Device];
                        else
                        {
                            restoreTo = ok ? cur : opt.RestoreFallback;
                            saved[m.Device] = restoreTo;
                        }
                        targets.Add(target);
                        log.V(string.Format(CultureInfo.InvariantCulture,
                            "dim plan: {0} cur={1} max={2} readOk={3} -> target={4} (restore to {5})",
                            m.Device, cur, max, ok, target, restoreTo));
                    }

                    // Persist restore targets BEFORE touching the monitors so a
                    // crash/kill can never leave a panel stranded at brightness 0.
                    // If the record cannot be persisted we must not darken anything:
                    // losing the original brightness is worse than not saving power.
                    if (!StateStore.Save(opt.StatePath, saved, StateStore.KindBrightness))
                    {
                        log.W("CRITICAL: could not persist " + opt.StatePath + "; refusing to dim");
                        saved.Clear();
                        foreach (KeyValuePair<string, uint> kv in savedBefore) saved[kv.Key] = kv.Value;
                        return;
                    }

                    int done = 0, issuedCount = 0;
                    object sync = new object();
                    if (opt.DryRun) { done = mons.Count; issuedCount = mons.Count; }
                    else unfinished = ParallelMonitors(mons, delegate(int i)
                    {
                        uint readBack;
                        bool issued;
                        bool verified = Ddc.WriteVcpVerified(mons[i].Handle, Native.VCP_BRIGHTNESS, targets[i], 3,
                                                             out readBack, out issued);
                        if (issued) { lock (sync) { issuedCount++; } }
                        if (verified)
                        {
                            lock (sync) { done++; }
                            log.W("dim ok: " + mons[i].Device + " brightness=" + readBack);
                        }
                        else
                        {
                            log.W(string.Format(CultureInfo.InvariantCulture,
                                "dim not verified: {0} wanted {1} readBack={2} writeIssued={3}",
                                mons[i].Device, targets[i], readBack, issued));
                        }
                    });

                    // Snapshot the counters under the workers' lock: a thread that blew the
                    // 8s join timeout may still be updating them.
                    int issuedCountSnapshot, doneSnapshot;
                    lock (sync) { issuedCountSnapshot = issuedCount; doneSnapshot = done; }

                    if (issuedCountSnapshot == 0)
                    {
                        // No write reached any panel in this round: nothing changed on
                        // screen, so drop only this round's additions - never a previous
                        // round's record for a panel that may still be sitting at 0.
                        saved.Clear();
                        foreach (KeyValuePair<string, uint> kv in savedBefore) saved[kv.Key] = kv.Value;
                        if (saved.Count == 0 && pendingRestore.Count == 0) StateStore.Clear(opt.StatePath);
                        else StateStore.Save(opt.StatePath, saved, StateStore.KindBrightness);
                        log.W("dim aborted: no monitor accepted the brightness write this round");
                        return;
                    }

                    if (opt.Mode == "off")
                    {
                        if (!opt.AllowPowerOff)
                        {
                            log.W("mode=off ignored: pass --allow-power-off (DDC power-off can strand a panel)");
                        }
                        else
                        {
                            foreach (PhysMon m in mons)
                            {
                                if (opt.DryRun) continue;
                                uint rb;
                                if (Ddc.WriteVcpVerified(m.Handle, Native.VCP_POWER_MODE, 4, 2, out rb))
                                    log.W("power-off ok: " + m.Device);
                                else
                                {
                                    log.W("power-off failed on " + m.Device + ", reverting");
                                    Ddc.WriteVcp(m.Handle, Native.VCP_POWER_MODE, 1, 2);
                                }
                                Thread.Sleep(120);
                            }
                        }
                    }

                    if (doneSnapshot < issuedCountSnapshot)
                    {
                        // The panels changed but their read-back did not match (clamping).
                        // Keep the dim state so the restore path runs and the original
                        // brightness recorded above is not thrown away.
                        log.W("dim partially verified: " + doneSnapshot + "/" + issuedCountSnapshot
                              + " panels confirmed; treating the rest as dimmed");
                    }

                    IsDark = true;
                    DimRound++;
                    LastDarkUtc = DateTime.UtcNow;

                    if (brightnessOnlyMode)
                    {
                        // "Only lower the brightness": backlight only, nothing else changes.
                        log.W("state=DARK (brightness only) monitors=" + doneSnapshot + "/" + targets.Count
                              + " level=" + opt.LockLevel);
                        // Panels that park a DDC brightness write until the link is
                        // renegotiated (Philips, verified) need this nudge to actually get
                        // dark. Same mode, so no resolution/refresh change.
                        NudgeModes();
                    }
                    else
                    {
                        // Refresh-rate reduction comes AFTER the backlight reached 0, so the
                        // monitor renegotiates the link while the panel is already dark and
                        // the user never sees the mode-change flash.
                        ApplyHz();
                        if (opt.Hz == 0) NudgeModes();   // no mode change above: force it

                        // Power plan last: nobody is waiting, and this is instant.
                        ApplyPlan();

                        log.W("state=DARK monitors=" + doneSnapshot + "/" + targets.Count + " level=" + opt.Level + " mode=" + opt.Mode);
                    }

                    WriteStatus();
                }
                finally
                {
                    // A worker that blew the join timeout is still using its handle. dxva2
                    // handle values are small and recycled, so destroying it here could
                    // hand that value to a different monitor mid-write. Leaking the handle
                    // until process exit is strictly safer.
                    if (unfinished == 0) foreach (PhysMon m in mons) m.Dispose();
                    else log.W("leaving " + unfinished + " monitor handle(s) undisposed: a DDC worker is stuck");
                }
            }
        }

        // ---- refresh rate ----
        /// <summary>
        /// Drop every display to opt.Hz. Runs while the panels are already dark, and the
        /// original rate is persisted BEFORE the change so a crash can always be undone.
        /// Never touches resolution or colour depth, so captures keep the same geometry.
        /// </summary>
        private void ApplyHz()
        {
            if (opt.Hz == 0) return;
            foreach (string dev in DisplayMode.Devices())
            {
                uint cur, dw, dh;
                if (!DisplayMode.TryGet(dev, out cur, out dw, out dh)) continue;
                if (cur <= opt.Hz) continue;                  // already at or below the target

                uint target = DisplayMode.PickHz(dev, opt.Hz);
                if (target == 0 || target >= cur)
                {
                    log.W("hz: no usable target at or below " + opt.Hz + "Hz on " + dev
                          + " (supported: " + DisplayMode.Format(DisplayMode.Supported(dev)) + ")");
                    continue;
                }

                if (!savedHz.ContainsKey(dev)) savedHz[dev] = cur;
                if (!StateStore.Save(opt.StateHzPath, savedHz, StateStore.KindHz))
                {
                    log.W("CRITICAL: could not persist the refresh-rate record; leaving " + dev + " at " + cur + "Hz");
                    savedHz.Remove(dev);
                    continue;
                }
                if (opt.DryRun) { log.V("hz (dry-run): " + dev + " " + cur + " -> " + target); continue; }

                if (DisplayMode.TrySetHz(dev, target))
                {
                    uint now, nw, nh;
                    DisplayMode.TryGet(dev, out now, out nw, out nh);
                    log.W("hz: " + dev + " " + cur + " -> " + now + "Hz (saved " + savedHz[dev] + "Hz)");
                }
                else log.W("hz: failed to set " + dev + " to " + target + "Hz");
            }
            // A DDC read immediately after a mode change fails while the link re-locks;
            // push the next reassert a full interval away instead of burning a dead probe.
            lastReassertUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Force every display to re-apply its current mode. Needed after a DDC brightness
        /// write on panels that only latch the new backlight level when the link is
        /// renegotiated; the resolution and refresh rate are unchanged, so this is just a
        /// mode "re-poke". Can be disabled with --no-mode-nudge.
        /// </summary>
        private void NudgeModes()
        {
            if (opt.NoModeNudge) { log.V("mode nudge skipped (--no-mode-nudge)"); return; }
            if (opt.DryRun) return;
            foreach (string dev in DisplayMode.Devices())
            {
                uint cur, w, h;
                if (!DisplayMode.TryGet(dev, out cur, out w, out h)) continue;

                // Re-applying the SAME mode is a driver no-op - verified: the Philips keeps
                // the old backlight until the timing actually changes. So hop to the nearest
                // different supported rate and straight back. Costs ~0.5 s of renegotiation
                // per display and ends on exactly the original refresh rate.
                List<uint> sup = DisplayMode.Supported(dev);
                uint alt = 0, best = UInt32.MaxValue;
                foreach (uint f in sup)
                {
                    if (f == cur) continue;
                    uint d = f > cur ? f - cur : cur - f;
                    if (d < best) { best = d; alt = f; }
                }
                if (alt == 0) { log.W("mode nudge: no alternative mode on " + dev); continue; }

                bool a = DisplayMode.TrySetHz(dev, alt);
                Thread.Sleep(300);
                bool b = DisplayMode.TrySetHz(dev, cur);
                Thread.Sleep(200);
                uint now, nw, nh;
                DisplayMode.TryGet(dev, out now, out nw, out nh);
                log.W("mode nudge: " + dev + " " + cur + " -> " + alt + " -> " + cur + "Hz"
                      + (a && b && now == cur ? " ok" : " INCOMPLETE (now " + now + "Hz)"));
            }
        }

        /// <summary>Put the refresh rates back. Called AFTER the backlight comes up, because a
        /// DDC transaction issued right after a mode change blocks for seconds.</summary>
        private bool RestoreHzAll()
        {
            if (savedHz.Count == 0) { if (pendingHz.Count == 0) StateStore.Clear(opt.StateHzPath); return true; }
            var still = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            var done = new List<string>();
            foreach (string dev in new List<string>(savedHz.Keys))
            {
                uint want = savedHz[dev];
                bool ok = false;
                if (!opt.DryRun)
                {
                    uint cur, dw, dh;
                    if (DisplayMode.TryGet(dev, out cur, out dw, out dh))
                    {
                        if (cur == want) ok = true;
                        else ok = DisplayMode.TrySetHz(dev, want)
                                  && DisplayMode.TryGet(dev, out cur, out dw, out dh) && cur == want;
                    }
                }
                else ok = true;
                if (ok) done.Add(dev);
                else { still[dev] = want; log.W("hz restore FAILED: " + dev + " -> wanted " + want + "Hz"); }
            }
            foreach (string dev in done) { savedHz.Remove(dev); log.W("hz restored: " + dev + " -> " + (opt.DryRun ? "(dry)" : "ok")); }
            pendingHz.Clear();
            foreach (KeyValuePair<string, uint> kv in still) pendingHz[kv.Key] = kv.Value;
            if (savedHz.Count == 0 && pendingHz.Count == 0) StateStore.Clear(opt.StateHzPath);
            // Measured: the DDC channel is usable again ~70 ms after ChangeDisplaySettingsEx
            // returns, so no long settle is needed here - WriteVcpVerified's own retries
            // cover the gap and cost nothing in the common case.
            return pendingHz.Count == 0;
        }

        private static string Join(List<uint> v)
        {
            return DisplayMode.Format(v);
        }

        // ---- power plan ----
        /// <summary>
        /// Downgrade the active power scheme. Switching the active scheme is a per-user
        /// setting (PowerSetActiveScheme with a NULL root key), so no elevation is needed.
        /// The original scheme is persisted BEFORE the switch so a crash can undo it.
        /// </summary>
        private void ApplyPlan()
        {
            if (String.IsNullOrEmpty(opt.SaverPlan) || opt.SaverPlanGuid == Guid.Empty) return;

            Guid? cur = PowerPlan.GetActive();
            if (!cur.HasValue) { log.W("plan: cannot read the active scheme; leaving it alone"); return; }
            if (cur.Value == opt.SaverPlanGuid) { log.V("plan: already on the saver scheme"); return; }

            if (!PlanStore.Save(opt.StatePlanPath, cur.Value))
            {
                log.W("CRITICAL: could not persist the original power scheme; not switching");
                return;
            }
            if (opt.DryRun) { log.V("plan (dry-run): would switch " + cur.Value + " -> " + opt.SaverPlanGuid); return; }

            if (PowerPlan.SetActive(opt.SaverPlanGuid))
                log.W("plan: " + cur.Value.ToString("D") + " -> saver " + opt.SaverPlanGuid.ToString("D")
                      + " (" + opt.SaverPlan + ")");
            else
            {
                log.W("plan: switch to the saver scheme FAILED; restoring the record");
                PlanStore.Clear(opt.StatePlanPath);
            }
        }

        /// <summary>Put the original power scheme back. Instant, so it runs first on wake.</summary>
        private bool RestorePlan()
        {
            Guid? want = PlanStore.Load(opt.StatePlanPath);
            if (!want.HasValue) return true;
            if (opt.DryRun) { PlanStore.Clear(opt.StatePlanPath); return true; }

            Guid? cur = PowerPlan.GetActive();
            if (cur.HasValue && cur.Value == want.Value)
            {
                PlanStore.Clear(opt.StatePlanPath);
                log.W("plan restored (already active): " + want.Value.ToString("D"));
                return true;
            }
            if (PowerPlan.SetActive(want.Value))
            {
                PlanStore.Clear(opt.StatePlanPath);
                log.W("plan restored: " + want.Value.ToString("D"));
                return true;
            }
            log.W("plan restore FAILED; keeping the record for retry: " + want.Value.ToString("D"));
            return false;
        }

        private void DoReassert()
        {
            lock (gate)
            {
                List<PhysMon> mons = Ddc.Enumerate();
                try
                {
                    foreach (PhysMon m in mons)
                    {
                        uint cur, max, type;
                        if (!Ddc.ReadVcp(m.Handle, Native.VCP_BRIGHTNESS, out cur, out max, out type))
                        {
                            log.V("reassert: read failed on " + m.Device);
                            continue;
                        }
                        if (!saved.ContainsKey(m.Device))
                        {
                            saved[m.Device] = cur;
                            if (!StateStore.Save(opt.StatePath, saved, StateStore.KindBrightness))
                                log.W("WARNING: reassert could not persist the recovery record for " + m.Device);
                            log.W("reassert: new monitor " + m.Device + " saved brightness=" + cur);
                        }
                        uint target = BrightnessLock ? opt.LockLevel : opt.Level;
                        if (target > max && max > 0) target = max;
                        if (cur != target && !opt.DryRun)
                        {
                            uint rb;
                            if (Ddc.WriteVcpVerified(m.Handle, Native.VCP_BRIGHTNESS, target, 2, out rb))
                                log.W("reassert: " + m.Device + " " + cur + " -> " + rb);
                            else
                                log.W("reassert FAILED: " + m.Device + " stayed at " + cur
                                      + " (wanted " + target + ")");
                        }
                    }
                }
                finally { foreach (PhysMon m in mons) m.Dispose(); }

                // A game or app can change the mode while we are dark; pull back only the
                // displays we actually lowered, never one we left alone.
                if (opt.Hz > 0 && !opt.DryRun)
                {
                    foreach (string dev in new List<string>(savedHz.Keys))
                    {
                        uint cur, dw, dh;
                        if (!DisplayMode.TryGet(dev, out cur, out dw, out dh)) continue;
                        uint target = DisplayMode.PickHz(dev, opt.Hz);
                        if (target > 0 && cur != target && DisplayMode.TrySetHz(dev, target))
                            log.W("reassert hz: " + dev + " " + cur + " -> " + target + "Hz");
                    }
                }
                WriteStatus();
            }
        }

        // ---- wake ----
        private void DoWake()
        {
            lock (gate)
            {
                RestoreFailures = 0;
                // Power scheme FIRST: it is instant, so the CPU is back at full speed before
                // anything else happens. Then brightness (visible in ~0.45 s), then the
                // refresh rate (a mode change costs ~1 s and needs a stable DDC link).
                bool planOk = RestorePlan();
                bool brightOnly = wantBrightnessOnly;
                bool allOk = TryRestoreAll(false);
                // Same quirk in reverse: a panel that only latches DDC brightness on a mode
                // change would stay dark after we wrote the original level back.
                if (brightOnly) NudgeModes();
                bool hzOk = RestoreHzAll();
                IsDark = false;
                LastWakeUtc = DateTime.UtcNow;
                WriteStatus();
                log.W("state=AWAKE restore=" + (allOk ? "complete" : "incomplete, will retry")
                      + " hz=" + (hzOk ? "restored" : "pending")
                      + " plan=" + (planOk ? "restored" : "pending"));
            }
        }

        /// <summary>
        /// The \\.\DISPLAYn device name is NOT stable: it can be renumbered by a GPU driver
        /// event, a monitor reconnect, or a mode change (observed live: DISPLAY1/2 became
        /// DISPLAY6/7 within one session). Every state file keys on that name, so a
        /// renumbering while dark would leave every panel unmatched - and therefore black.
        /// If nothing matches but the panel count is identical, map them back by order.
        /// Returns true if a remap happened.
        /// </summary>
        private static bool RemapToDevices(Dictionary<string, uint> map, List<PhysMon> mons, Log log)
        {
            if (map.Count == 0 || mons.Count != map.Count) return false;
            foreach (PhysMon m in mons) if (map.ContainsKey(m.Device)) return false;   // some matched: fine

            var keys = new List<string>(map.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            var devs = new List<string>();
            foreach (PhysMon m in mons) devs.Add(m.Device);
            devs.Sort(StringComparer.OrdinalIgnoreCase);

            var mapped = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < keys.Count; i++)
            {
                mapped[devs[i]] = map[keys[i]];
                log.W("renumbered: display names changed while dark; mapping " + keys[i] + " -> " + devs[i]
                      + " (value " + map[keys[i]] + ")");
            }
            map.Clear();
            foreach (KeyValuePair<string, uint> kv in mapped) map[kv.Key] = kv.Value;
            return true;
        }

        private bool TryRestoreAll(bool finalAttempt)
        {
            List<PhysMon> mons = Ddc.Enumerate();
            RemapToDevices(saved, mons, log);
            var still = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            var restoredDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var restoreLog = new List<string>();
            var sync = new object();
            int unfinished = 0;
            try
            {
                if (!opt.DryRun)
                {
                    // Resolve the per-monitor target first (reads are cheap enough and
                    // keep the concurrent section purely transactional).
                    var want = new uint[mons.Count];
                    var skip = new bool[mons.Count];
                    for (int i = 0; i < mons.Count; i++)
                    {
                        uint w;
                        if (!saved.TryGetValue(mons[i].Device, out w))
                        {
                            // Last-resort safety net: we know something WAS dimmed, so an
                            // unmatched panel must still be brought back. Restoring it to the
                            // fallback level is far better than leaving it black.
                            if (saved.Count > 0)
                            {
                                w = opt.RestoreFallback;
                                saved[mons[i].Device] = w;
                                log.W("restore: no record for " + mons[i].Device
                                      + " - using fallback brightness " + w);
                            }
                            else { skip[i] = true; continue; }
                        }
                        want[i] = Ddc.ClampToMax(mons[i].Handle, w);
                    }

                    unfinished = ParallelMonitors(mons, delegate(int i)
                    {
                        if (skip[i]) return;
                        uint rb;
                        if (Ddc.WriteVcpVerified(mons[i].Handle, Native.VCP_BRIGHTNESS, want[i], 4, out rb))
                        {
                            lock (sync) { restoredDevices.Add(mons[i].Device); restoreLog.Add(mons[i].Device + "=" + rb); }
                        }
                        else
                        {
                            lock (sync)
                            {
                                still[mons[i].Device] = saved[mons[i].Device];
                                RestoreFailures++;
                            }
                            log.W(string.Format(CultureInfo.InvariantCulture,
                                "restore FAILED: {0} wanted {1} readBack={2}", mons[i].Device, want[i], rb));
                        }
                    });
                }

                if (opt.Mode == "off" && opt.AllowPowerOff)
                {
                    foreach (PhysMon m in mons)
                    {
                        uint rb;
                        Ddc.WriteVcpVerified(m.Handle, Native.VCP_POWER_MODE, 1, 2, out rb);
                    }
                }
            }
            finally
            {
                // Workers that did not finish still hold their handle; see ParallelMonitors.
                if (unfinished == 0) foreach (PhysMon m in mons) m.Dispose();
                else log.W("leaving " + unfinished + " monitor handle(s) undisposed: a DDC worker is stuck");
            }

            // A worker that blew the join timeout may still be running. Snapshot the
            // shared results under the same lock they use, so the reads below never race
            // a straggler writing into these collections.
            Dictionary<string, uint> stillSnapshot;
            HashSet<string> restoredSnapshot;
            List<string> logSnapshot;
            lock (sync)
            {
                stillSnapshot = new Dictionary<string, uint>(still, StringComparer.OrdinalIgnoreCase);
                restoredSnapshot = new HashSet<string>(restoredDevices, StringComparer.OrdinalIgnoreCase);
                logSnapshot = new List<string>(restoreLog);
            }

            foreach (string r in logSnapshot) log.W("restore ok: " + r);

            // A device that is back at its original brightness is DONE - drop it, so
            // `saved` tracks exactly what is still outstanding. Keeping it would make the
            // state file undeletable (the clear gate needs saved.Count == 0) and would
            // re-apply a stale value on the next dim instead of re-reading the panel.
            foreach (string d in restoredSnapshot) saved.Remove(d);

            // Anything still recorded but NOT successfully written back this pass stays
            // pending - including a panel that is not currently enumerated (unplugged,
            // or too slow to answer DDC right now). Dropping it here would throw away
            // that panel's only restore value while it still sits at brightness 0.
            foreach (KeyValuePair<string, uint> kv in saved)
                if (!restoredSnapshot.Contains(kv.Key) && !stillSnapshot.ContainsKey(kv.Key))
                    stillSnapshot[kv.Key] = kv.Value;

            pendingRestore.Clear();
            foreach (KeyValuePair<string, uint> kv in stillSnapshot) pendingRestore[kv.Key] = kv.Value;

            if (pendingRestore.Count == 0)
            {
                saved.Clear();
                StateStore.Clear(opt.StatePath);
                return true;
            }
            if (finalAttempt)
                log.W("restore still pending at shutdown: " + pendingRestore.Count + " monitor(s); next start will retry");
            return false;
        }

        private void RetryPendingRestore()
        {
            lock (gate)
            {
                List<PhysMon> mons = Ddc.Enumerate();
                try
                {
                    foreach (PhysMon m in mons)
                    {
                        uint want;
                        if (!pendingRestore.TryGetValue(m.Device, out want)) continue;
                        uint rb;
                        if (Ddc.WriteVcpVerified(m.Handle, Native.VCP_BRIGHTNESS, Ddc.ClampToMax(m.Handle, want), 2, out rb))
                        {
                            log.W("retry restore ok: " + m.Device + " brightness=" + rb);
                            pendingRestore.Remove(m.Device);
                            saved.Remove(m.Device);
                        }
                    }
                    if (pendingRestore.Count == 0 && saved.Count == 0)
                        StateStore.Clear(opt.StatePath);

                    // same retry for a refresh rate that would not come back
                    if (pendingHz.Count > 0 && !opt.DryRun)
                    {
                        foreach (string dev in new List<string>(pendingHz.Keys))
                        {
                            uint want = pendingHz[dev], cur, dw, dh;
                            if (!DisplayMode.TryGet(dev, out cur, out dw, out dh)) continue;
                            if (cur == want
                                || (DisplayMode.TrySetHz(dev, want)
                                    && DisplayMode.TryGet(dev, out cur, out dw, out dh) && cur == want))
                            {
                                log.W("retry hz ok: " + dev + " -> " + want + "Hz");
                                pendingHz.Remove(dev);
                                savedHz.Remove(dev);
                            }
                        }
                        if (pendingHz.Count == 0 && savedHz.Count == 0) StateStore.Clear(opt.StateHzPath);
                    }
                }
                finally { foreach (PhysMon m in mons) m.Dispose(); }
            }
        }

        private void WriteStatus()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("state=" + (IsDark ? "dark" : "awake"));
                sb.AppendLine("pid=" + System.Diagnostics.Process.GetCurrentProcess().Id);
                sb.AppendLine("time=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                sb.AppendLine("idleSec=" + opt.IdleSec + " level=" + opt.Level + " mode=" + opt.Mode + " wake=" + opt.Wake);
                sb.AppendLine("dimRounds=" + DimRound + " restoreFailures=" + RestoreFailures);
                sb.AppendLine("lastDarkUtc=" + (LastDarkUtc == DateTime.MinValue ? "-" : LastDarkUtc.ToString("s", CultureInfo.InvariantCulture)));
                sb.AppendLine("lastWakeUtc=" + (LastWakeUtc == DateTime.MinValue ? "-" : LastWakeUtc.ToString("s", CultureInfo.InvariantCulture)));
                foreach (KeyValuePair<string, uint> kv in saved)
                    sb.AppendLine("savedRestore|" + kv.Key + "|" + kv.Value);
                foreach (KeyValuePair<string, uint> kv in savedHz)
                    sb.AppendLine("savedHz|" + kv.Key + "|" + kv.Value);
                Guid? actPlan = PowerPlan.GetActive();
                sb.AppendLine("activePlan|" + (actPlan.HasValue ? actPlan.Value.ToString("D") : "-"));
                Guid? savedPlan = PlanStore.Load(opt.StatePlanPath);
                if (savedPlan.HasValue) sb.AppendLine("savedPlan|" + savedPlan.Value.ToString("D"));
                foreach (string dev in DisplayMode.Devices())
                {
                    uint hz, w, h;
                    if (DisplayMode.TryGet(dev, out hz, out w, out h))
                        sb.AppendLine("currentHz|" + dev + "|" + hz);
                }
                Directory.CreateDirectory(opt.RootDir);
                File.WriteAllText(opt.StatusPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>Called on startup: undo a dark state left behind by a crash/kill.</summary>
        public void RecoverStrandedState()
        {
            // The power scheme first: it is instant, and leaving the machine on a saver
            // scheme after a crash is the most noticeable side effect.
            RestorePlan();

            // Brightness next, then refresh rate - same reasoning as the wake path: a DDC
            // transaction immediately after a mode change blocks for seconds.
            Dictionary<string, uint> st = StateStore.Load(opt.StatePath, StateStore.KindBrightness);
            if (st.Count == 0)
            {
                // There can still be a refresh-rate record to undo; do NOT return here.
                st = null;
            }
            else
            {
                log.W("found stranded state from a previous run (" + st.Count + " monitor(s)), restoring");
            }
            List<PhysMon> mons = Ddc.Enumerate();
            // Same renumbering hazard as the wake path: a crash + a driver event can leave
            // the record keyed on names that no longer exist.
            if (st != null) RemapToDevices(st, mons, log);
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (st != null)
                foreach (PhysMon m in mons)
                {
                    uint want;
                    if (!st.TryGetValue(m.Device, out want))
                    {
                        // Never leave a panel dark just because its record is missing.
                        want = opt.RestoreFallback;
                        log.W("recovery: no record for " + m.Device + " - using fallback " + want);
                    }
                    uint rb;
                    if (Ddc.WriteVcpVerified(m.Handle, Native.VCP_BRIGHTNESS, Ddc.ClampToMax(m.Handle, want), 3, out rb))
                    {
                        done.Add(m.Device);
                        log.W("recovered " + m.Device + " -> " + rb);
                    }
                    else log.W("recover failed for " + m.Device);

                    // Undo a DDC power-off too; a panel left off would otherwise look
                    // "recovered" while staying dark.
                    uint prb;
                    Ddc.WriteVcpVerified(m.Handle, Native.VCP_POWER_MODE, 1, 2, out prb);
                }
            }
            finally { foreach (PhysMon m in mons) m.Dispose(); }

            // Only drop the record when every recorded panel was restored. A panel that
            // is not enumerated right now (unplugged, DDC slow during boot) keeps its
            // entry so a later start can still fix it.
            if (st != null)
            {
                bool all = true;
                foreach (string dev in st.Keys) if (!done.Contains(dev)) { all = false; log.W("recovery pending for " + dev); }
                if (all) StateStore.Clear(opt.StatePath);
            }

            // Refresh rate last (see the ordering note above).
            Dictionary<string, uint> hzState = StateStore.Load(opt.StateHzPath, StateStore.KindHz);
            if (hzState.Count > 0)
            {
                log.W("found stranded refresh-rate state (" + hzState.Count + " display(s)), restoring");
                var hzDone = new List<string>();
                foreach (string dev in new List<string>(hzState.Keys))
                {
                    uint want = hzState[dev], cur, dw, dh;
                    if (DisplayMode.TryGet(dev, out cur, out dw, out dh)
                        && (cur == want || (DisplayMode.TrySetHz(dev, want)
                            && DisplayMode.TryGet(dev, out cur, out dw, out dh) && cur == want)))
                    {
                        hzDone.Add(dev);
                        log.W("hz recovered " + dev + " -> " + want + "Hz");
                    }
                    else log.W("hz recovery pending for " + dev + " (wanted " + want + "Hz)");
                }
                foreach (string dev in hzDone) hzState.Remove(dev);
                if (hzState.Count == 0) StateStore.Clear(opt.StateHzPath);
                else log.W("hz recovery incomplete: " + hzState.Count + " display(s) still pending");
            }
        }
    }

    // ----------------------------------------------------------- metrics ------
    internal sealed class Metrics
    {
        private readonly object gate = new object();
        private ulong lastIdle, lastKernel, lastUser;
        private DateTime lastAt = DateTime.MinValue;
        private double cpu;
        private IntPtr nvmlDev = IntPtr.Zero;
        private bool nvmlOk;
        private string gpuName = "";

        public Metrics()
        {
            try
            {
                if (Native.nvmlInit_v2() == 0 && Native.nvmlDeviceGetHandleByIndex_v2(0, out nvmlDev) == 0)
                {
                    nvmlOk = nvmlDev != IntPtr.Zero;
                    var sb = new StringBuilder(96);
                    if (nvmlOk && Native.nvmlDeviceGetName(nvmlDev, sb, 96) == 0) gpuName = sb.ToString();
                }
            }
            catch { nvmlOk = false; }
        }

        /// <summary>CPU busy % computed from GetSystemTimes deltas (no PerformanceCounter).</summary>
        public double Cpu()
        {
            lock (gate)
            {
                Native.FILETIME i, k, u;
                if (!Native.GetSystemTimes(out i, out k, out u)) return cpu;
                ulong idle = ((ulong)i.dwHighDateTime << 32) | i.dwLowDateTime;
                ulong kern = ((ulong)k.dwHighDateTime << 32) | k.dwLowDateTime;
                ulong user = ((ulong)u.dwHighDateTime << 32) | u.dwLowDateTime;

                if (lastAt != DateTime.MinValue)
                {
                    ulong dIdle = idle - lastIdle, dTotal = (kern - lastKernel) + (user - lastUser);
                    // kernel time already includes idle time, so this is the whole interval
                    if (dTotal > 0) cpu = 100.0 * (double)(dTotal - Math.Min(dIdle, dTotal)) / dTotal;
                }
                lastIdle = idle; lastKernel = kern; lastUser = user;
                lastAt = DateTime.UtcNow;
                return cpu;
            }
        }

        public void Memory(out double usedGb, out double totalGb)
        {
            var m = new Native.MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
            usedGb = 0; totalGb = 0;
            if (!Native.GlobalMemoryStatusEx(ref m)) return;
            totalGb = m.ullTotalPhys / 1073741824.0;
            usedGb = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
        }

        public bool Gpu(out double util, out double watts)
        {
            util = 0; watts = 0;
            if (!nvmlOk) return false;
            try
            {
                Native.NVML_UTILIZATION r;
                if (Native.nvmlDeviceGetUtilizationRates(nvmlDev, out r) == 0) util = r.gpu;
                else return false;
                uint mw;
                if (Native.nvmlDeviceGetPowerUsage(nvmlDev, out mw) == 0) watts = mw / 1000.0;
                else watts = -1;
                return true;
            }
            catch { return false; }
        }

        public string GpuName { get { return gpuName; } }
    }

    // -------------------------------------------------------------- web ui ----
    // A tiny HTTP/1.1 server on 127.0.0.1. Deliberately NOT HttpListener: that needs a
    // URL ACL (netsh http add urlacl) for a non-elevated process, whereas a plain
    // TcpListener on a loopback port needs nothing at all.
    internal sealed class WebUi
    {
        private readonly int port;
        private readonly Engine engine;
        private readonly Options opt;
        private readonly Log log;
        private readonly Metrics metrics = new Metrics();
        private readonly Dictionary<string, string> planNames = new Dictionary<string, string>();
        private TcpListener listener;
        private Thread thread;
        private volatile bool running;
        private string page;

        public WebUi(int port, Engine engine, Options opt, Log log)
        {
            this.port = port; this.engine = engine; this.opt = opt; this.log = log;
            try
            {
                foreach (KeyValuePair<Guid, string> kv in PowerPlan.List())
                    planNames[kv.Key.ToString("D").ToLowerInvariant()] = kv.Value;
            }
            catch { }
            page = LoadPage();
            // Prime the CPU sampler so the very first /api/state already has a delta.
            try { metrics.Cpu(); } catch { }
        }

        private static string LoadPage()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                foreach (string n in asm.GetManifestResourceNames())
                    if (n.EndsWith("ui.html", StringComparison.OrdinalIgnoreCase))
                        using (Stream s = asm.GetManifestResourceStream(n))
                        using (var sr = new StreamReader(s, Encoding.UTF8))
                            return sr.ReadToEnd();
            }
            catch { }
            return "<html><body style='font:14px sans-serif;background:#0f1216;color:#e6edf3;padding:24px'>"
                 + "<h2>ui.html 缺失</h2><p>重新运行 build.ps1 把界面资源编译进 exe。</p></body></html>";
        }

        public bool Start()
        {
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                running = true;
                thread = new Thread(Loop);
                thread.IsBackground = true;
                thread.Name = "web-ui";
                thread.Start();
                log.W("web ui listening on http://127.0.0.1:" + port + "/");
                return true;
            }
            catch (Exception ex)
            {
                log.W("web ui disabled: cannot listen on 127.0.0.1:" + port + " - " + ex.Message);
                return false;
            }
        }

        public void Stop() { running = false; try { if (listener != null) listener.Stop(); } catch { } }

        private void Loop()
        {
            while (running)
            {
                try
                {
                    TcpClient c = listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(delegate(object o) { Serve((TcpClient)o); }, c);
                }
                catch
                {
                    if (!running) break;
                    Thread.Sleep(50);
                }
            }
        }

        private void Serve(TcpClient client)
        {
            try
            {
                using (client)
                {
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                    NetworkStream ns = client.GetStream();

                    // Read headers byte-wise: StreamReader would buffer past the header and
                    // swallow any request body.
                    var head = new List<byte>();
                    var one = new byte[1];
                    while (true)
                    {
                        int n = ns.Read(one, 0, 1);
                        if (n <= 0) return;
                        head.Add(one[0]);
                        int len = head.Count;
                        if (len >= 4 && head[len - 4] == 13 && head[len - 3] == 10 && head[len - 2] == 13 && head[len - 1] == 10)
                            break;
                        if (len > 16384) return;
                    }
                    string header = Encoding.ASCII.GetString(head.ToArray());
                    string[] lines = header.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                    if (lines.Length == 0) return;
                    string[] req = lines[0].Split(' ');
                    if (req.Length < 2) return;
                    string method = req[0], path = req[1];
                    int q = path.IndexOf('?');
                    if (q >= 0) path = path.Substring(0, q);

                    int status = 200;
                    string body; string ctype;

                    if (method == "GET" && (path == "/" || path == "/index.html"))
                    {
                        body = page; ctype = "text/html; charset=utf-8";
                    }
                    else if (method == "GET" && path == "/api/state")
                    {
                        body = StateJson(); ctype = "application/json; charset=utf-8";
                    }
                    else if (method == "POST" && path.StartsWith("/api/"))
                    {
                        string[] seg = path.Trim('/').Split('/');
                        // /api/{manual|auto|lock}/{on|off}
                        if (seg.Length == 3 && (seg[1] == "manual" || seg[1] == "auto" || seg[1] == "lock")
                            && (seg[2] == "on" || seg[2] == "off"))
                        {
                            bool on = seg[2] == "on";
                            if (seg[1] == "manual")
                            {
                                engine.ManualMode = on;
                                log.W("web: manual power-save " + (on ? "ON" : "OFF"));
                                if (on) { engine.BrightnessLock = false; engine.RequestDark(); }
                                else engine.RequestAwake();
                            }
                            else if (seg[1] == "lock")
                            {
                                // Brightness-only lock. Mouse movement will NOT undo it;
                                // a keypress (or this button) is the way back.
                                engine.BrightnessLock = on;
                                if (on) engine.ManualMode = false;
                                log.W("web: brightness lock " + (on ? "ON (mouse ignored)" : "OFF"));
                                if (on) engine.RequestDark(opt.LockBrightnessOnly); else engine.RequestAwake();
                            }
                            else
                            {
                                engine.AutoEnabled = on;
                                log.W("web: idle auto power-save " + (on ? "ON" : "OFF"));
                                if (!on && !engine.ManualMode && !engine.BrightnessLock) engine.RequestAwake();
                            }
                            body = StateJson(); ctype = "application/json; charset=utf-8";
                        }
                        else { status = 404; body = "{\"error\":\"not found\"}"; ctype = "application/json"; }
                    }
                    else { status = 404; body = "not found"; ctype = "text/plain; charset=utf-8"; }

                    byte[] payload = Encoding.UTF8.GetBytes(body);
                    var sb = new StringBuilder();
                    sb.Append("HTTP/1.1 ").Append(status).Append(" ").Append(status == 200 ? "OK" : "Not Found").Append("\r\n");
                    sb.Append("Content-Type: ").Append(ctype).Append("\r\n");
                    sb.Append("Content-Length: ").Append(payload.Length).Append("\r\n");
                    sb.Append("Cache-Control: no-store\r\n");
                    sb.Append("Connection: close\r\n\r\n");
                    byte[] hb = Encoding.ASCII.GetBytes(sb.ToString());
                    ns.Write(hb, 0, hb.Length);
                    ns.Write(payload, 0, payload.Length);
                    ns.Flush();
                }
            }
            catch { }
        }

        private string StateJson()
        {
            var sb = new StringBuilder();
            sb.Append('{');

            double cpu = metrics.Cpu();
            double ramUsed, ramTotal;
            metrics.Memory(out ramUsed, out ramTotal);
            double gpuUtil, gpuW;
            bool gpuOk = metrics.Gpu(out gpuUtil, out gpuW);

            var bright = new List<int>();
            var hzs = new List<uint>();
            try
            {
                List<PhysMon> mons = Ddc.Enumerate();
                try
                {
                    foreach (PhysMon m in mons)
                    {
                        uint cur, max, type;
                        bright.Add(Ddc.ReadVcp(m.Handle, Native.VCP_BRIGHTNESS, out cur, out max, out type) ? (int)cur : -1);
                    }
                }
                finally { foreach (PhysMon m in mons) m.Dispose(); }
            }
            catch { }
            try
            {
                foreach (string dev in DisplayMode.Devices())
                {
                    uint hz, w, h;
                    if (DisplayMode.TryGet(dev, out hz, out w, out h)) hzs.Add(hz);
                }
            }
            catch { }
            if (bright.Count == 0) bright.Add(-1);
            if (hzs.Count == 0) hzs.Add(0);

            string plan = "?";
            try
            {
                Guid? ap = PowerPlan.GetActive();
                if (ap.HasValue)
                {
                    string key = ap.Value.ToString("D").ToLowerInvariant();
                    plan = planNames.ContainsKey(key) ? planNames[key] : key;
                }
            }
            catch { }

            J(sb, "dark", engine.IsDark ? "true" : "false");
            J(sb, "manual", engine.ManualMode ? "true" : "false");
            J(sb, "lock", engine.BrightnessLock ? "true" : "false");
            J(sb, "dimBrightnessOnly", engine.IsDimBrightnessOnly ? "true" : "false");
            J(sb, "auto", engine.AutoEnabled ? "true" : "false");
            N(sb, "lockLevel", opt.LockLevel);
            N(sb, "idleSec", engine.IdleSeconds());
            N(sb, "peekSec", engine.PeekSec);
            N(sb, "idleTarget", opt.IdleSec);
            A(sb, "brightness", string.Join(",", bright.ConvertAll(x => x.ToString()).ToArray()));
            A(sb, "hz", string.Join(",", hzs.ConvertAll(x => x.ToString()).ToArray()));
            S(sb, "plan", plan);
            N(sb, "cpu", Math.Round(cpu, 1));
            if (gpuOk) { N(sb, "gpu", Math.Round(gpuUtil, 1)); N(sb, "gpuPower", gpuW < 0 ? -1 : Math.Round(gpuW, 1)); }
            else { sb.Append("\"gpu\":null,\"gpuPower\":null,"); }
            S(sb, "gpuName", metrics.GpuName);
            N(sb, "ramUsed", Math.Round(ramUsed, 1));
            N(sb, "ramTotal", Math.Round(ramTotal, 1));
            N(sb, "pid", Process.GetCurrentProcess().Id);

            if (sb.Length > 0 && sb[sb.Length - 1] == ',') sb.Length--;
            sb.Append('}');
            return sb.ToString();
        }

        private static void J(StringBuilder sb, string k, string v) { sb.Append('"').Append(k).Append("\":").Append(v).Append(','); }
        private static void N(StringBuilder sb, string k, double v)
        { sb.Append('"').Append(k).Append("\":").Append(v.ToString("0.###", CultureInfo.InvariantCulture)).Append(','); }
        private static void A(StringBuilder sb, string k, string csv) { sb.Append('"').Append(k).Append("\":[").Append(csv).Append("],"); }
        private static void S(StringBuilder sb, string k, string v)
        {
            sb.Append('"').Append(k).Append("\":\"");
            foreach (char c in v ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < ' ') sb.Append(' ');
                else sb.Append(c);
            }
            sb.Append("\",");
        }
    }

    // ------------------------------------------------------------ program -----
    internal static class Program
    {
        private static string reportPath;
        private static readonly object sayGate = new object();

        /// <summary>Held for the daemon's lifetime. A static field keeps it rooted.</summary>
        private const string DaemonMutexName = "Local\\ScreenDim.Daemon";
        private static Mutex daemonLock;

        internal static void Say(string s)
        {
            try { Console.WriteLine(s); } catch { }
            if (reportPath != null)
            {
                lock (sayGate)
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(reportPath));
                        File.AppendAllText(reportPath, s + Environment.NewLine, Encoding.UTF8);
                    }
                    catch { }
                }
            }
        }

        private static int Main(string[] args)
        {
            Options opt = Options.Parse(args);
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            Directory.CreateDirectory(opt.RootDir);
            Log log = new Log(opt.LogPath, opt.Verbose);

            if (opt.Command == "help")
            {
                PrintHelp();
                return opt.UnknownOption ? 2 : 0;
            }

            if (opt.Command != "daemon")
            {
                reportPath = opt.ReportPath;
                try { File.WriteAllText(opt.ReportPath, "", Encoding.UTF8); } catch { }
            }

            switch (opt.Command)
            {
                case "list": return CmdList(log);
                case "hz-list": return CmdHzList();
                case "plan-list": return CmdPlanList();
                case "hz-test": return CmdHzTest(opt, log);
                case "status": return CmdStatus(opt);
                case "selftest": return CmdSelftest(opt, log);
                case "dim": return CmdControl(opt, "dim");
                case "wake": return CmdControl(opt, "wake");
                case "stop": return CmdControl(opt, "stop");
                case "restore-saved": return CmdRestoreSaved(opt, log);
                case "daemon": return CmdDaemon(opt, log);
                default: PrintHelp(); return 0;
            }
        }

        // ---- list ----
        private static int CmdList(Log log)
        {
            Say("== physical monitors and DDC/CI capabilities ==");
            List<PhysMon> mons = Ddc.Enumerate();
            int n = 0;
            foreach (PhysMon m in mons)
            {
                n++;
                uint bc, bm, bt, pc, pm, pt;
                bool bOk = Ddc.ReadVcp(m.Handle, Native.VCP_BRIGHTNESS, out bc, out bm, out bt);
                bool pOk = Ddc.ReadVcp(m.Handle, Native.VCP_POWER_MODE, out pc, out pm, out pt);
                Say(string.Format(CultureInfo.InvariantCulture,
                    "[{0}] device={1} desc=\"{2}\" handle=0x{3:X}", n, m.Device, m.Desc, m.Handle.ToInt64()));
                Say(string.Format(CultureInfo.InvariantCulture,
                    "     VCP 0x10 brightness: ok={0} current={1} max={2} (min assumed 0) type={3}",
                    bOk, bOk ? bc.ToString(CultureInfo.InvariantCulture) : "-",
                    bOk ? bm.ToString(CultureInfo.InvariantCulture) : "-", bt));
                Say(string.Format(CultureInfo.InvariantCulture,
                    "     VCP 0xD6 power     : ok={0} current={1} max={2}",
                    pOk, pOk ? pc.ToString(CultureInfo.InvariantCulture) : "-",
                    pOk ? pm.ToString(CultureInfo.InvariantCulture) : "-"));
            }
            foreach (PhysMon m in mons) m.Dispose();
            Say("total=" + n + " monitor(s)");
            Say("virtual desktop=" + Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN) + "x" + Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));
            return n > 0 ? 0 : 1;
        }

        // ---- status ----
        private static int CmdStatus(Options opt)
        {
            Say("== ScreenDim status ==");

            // The status file outlives the process, so report liveness separately rather
            // than showing a dead run's "state=dark" as if it were current.
            bool createdNew;
            var probe = new Mutex(true, DaemonMutexName, out createdNew);
            bool alive = !createdNew;
            probe.Close();
            Say("daemon: " + (alive ? "RUNNING" : "not running"));

            if (!File.Exists(opt.StatusPath))
            {
                Say(alive ? "no status file yet (daemon just started?)" : "no status file");
            }
            else
            {
                Say("-- last written status " + (alive ? "" : "(STALE - daemon is not running) ") + "--");
                Say(File.ReadAllText(opt.StatusPath, Encoding.UTF8).Trim());
            }
            Say("state file: " + (File.Exists(opt.StatePath) ? File.ReadAllText(opt.StatePath, Encoding.UTF8).Trim() : "(none)"));
            return alive ? 0 : 1;
        }

        // ---- control file ----
        private static int CmdControl(Options opt, string verb)
        {
            Directory.CreateDirectory(opt.RootDir);
            File.WriteAllText(opt.CtrlPath, verb + "|" + DateTime.Now.ToString("s", CultureInfo.InvariantCulture), Encoding.UTF8);
            Say("wrote control command: " + verb);
            Say("(a running daemon picks this up within " + opt.TickMs + " ms)");
            return 0;
        }

        // ---- restore from the persisted state file ----
        private static int CmdRestoreSaved(Options opt, Log log)
        {
            // Hold the daemon lock for the WHOLE operation, not just the check: releasing
            // it after the probe would let a daemon start mid-restore and have its freshly
            // written recovery record deleted by our StateStore.Clear.
            bool createdNew;
            var hold = new Mutex(true, DaemonMutexName, out createdNew);
            if (!createdNew)
            {
                Say("a ScreenDim daemon is running and owns the restore state.");
                Say("use --wake-now to light the panels, or --stop first.");
                hold.Close();
                return 2;
            }
            try
            {
                // Power scheme first: instant, and the most noticeable thing to leave behind.
                Guid? wantPlan = PlanStore.Load(opt.StatePlanPath);
                if (wantPlan.HasValue)
                {
                    Guid? curPlan = PowerPlan.GetActive();
                    if ((curPlan.HasValue && curPlan.Value == wantPlan.Value) || PowerPlan.SetActive(wantPlan.Value))
                    {
                        Say("restored power scheme -> " + wantPlan.Value.ToString("D"));
                        PlanStore.Clear(opt.StatePlanPath);
                    }
                    else Say("FAILED to restore power scheme " + wantPlan.Value.ToString("D") + " (record kept)");
                }

                Dictionary<string, uint> st = StateStore.Load(opt.StatePath, StateStore.KindBrightness);
                if (st.Count == 0) Say("no brightness record to restore");

                List<PhysMon> mons = Ddc.Enumerate();
                var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int unfinished = 0;
                try
                {
                    // Clamp like every other restore path, otherwise a recorded value above
                    // the current max can never verify and this command would never finish.
                    var want = new uint[mons.Count];
                    for (int i = 0; i < mons.Count; i++)
                    {
                        uint w;
                        if (!st.TryGetValue(mons[i].Device, out w)) { want[i] = 0; continue; }
                        want[i] = Ddc.ClampToMax(mons[i].Handle, w);
                    }

                    for (int i = 0; i < mons.Count; i++)
                    {
                        if (want[i] == 0) continue;
                        uint rb;
                        if (Ddc.WriteVcpVerified(mons[i].Handle, Native.VCP_BRIGHTNESS, want[i], 4, out rb))
                        {
                            Say("restored " + mons[i].Device + " -> brightness " + rb);
                            done.Add(mons[i].Device);
                        }
                        else Say("FAILED to restore " + mons[i].Device);

                        Ddc.WriteVcpVerified(mons[i].Handle, Native.VCP_POWER_MODE, 1, 2, out rb);
                    }
                }
                finally
                {
                    if (unfinished == 0) foreach (PhysMon m in mons) m.Dispose();
                }

                // Only drop the record when every recorded panel was restored. A partial
                // restore must keep the remaining entries: they are the only copy of those
                // panels' original brightness.
                foreach (string dev in st.Keys)
                    if (!done.Contains(dev)) Say("still pending (not restored, or not connected now): " + dev);

                bool all = done.Count == st.Count;
                if (st.Count > 0)
                {
                    if (all) StateStore.Clear(opt.StatePath);
                    else Say("state file kept so the remaining panels can be fixed later");
                }

                // Refresh rate LAST: a DDC transaction right after a mode change blocks for
                // seconds, so the brightness has to be done first.
                bool hzAll = true;
                Dictionary<string, uint> hzState = StateStore.Load(opt.StateHzPath, StateStore.KindHz);
                foreach (string dev in new List<string>(hzState.Keys))
                {
                    uint want = hzState[dev], cur, dw, dh;
                    if (DisplayMode.TryGet(dev, out cur, out dw, out dh)
                        && (cur == want || (DisplayMode.TrySetHz(dev, want)
                            && DisplayMode.TryGet(dev, out cur, out dw, out dh) && cur == want)))
                    {
                        Say("restored " + dev + " -> " + want + "Hz");
                        hzState.Remove(dev);
                    }
                    else { hzAll = false; Say("FAILED to restore refresh rate on " + dev); }
                }
                if (hzState.Count == 0) StateStore.Clear(opt.StateHzPath);
                else Say("refresh-rate record kept: " + string.Join(", ", new List<string>(hzState.Keys).ToArray()));

                bool planOk = !File.Exists(opt.StatePlanPath);
                return (all && hzAll && planOk) ? 0 : 1;
            }
            finally
            {
                try { hold.ReleaseMutex(); } catch { }
                hold.Close();
            }
        }

        // ---- refresh-rate diagnostics ----
        private static int CmdHzList()
        {
            Say("== display refresh rates ==");
            List<string> devs = DisplayMode.Devices();
            foreach (string dev in devs)
            {
                uint hz, w, h;
                if (!DisplayMode.TryGet(dev, out hz, out w, out h))
                {
                    Say(dev + ": could not read the display mode");
                    continue;
                }
                Say(string.Format(CultureInfo.InvariantCulture,
                    "{0}: {1}x{2} @ {3}Hz   supported at this resolution: {4}",
                    dev, w, h, hz, DisplayMode.Format(DisplayMode.Supported(dev))));
            }
            Say("displays=" + devs.Count);
            return devs.Count > 0 ? 0 : 1;
        }

        private static int CmdHzTest(Options opt, Log log)
        {
            Say("== refresh-rate change test ==");
            if (opt.Hz == 0) { Say("--hz 0: nothing to test"); return 0; }

            List<string> devs = DisplayMode.Devices();
            if (devs.Count == 0) { Say("FAIL: no displays"); return 1; }

            var original = new Dictionary<string, uint>();
            foreach (string dev in devs)
            {
                uint hz, w, h;
                if (DisplayMode.TryGet(dev, out hz, out w, out h))
                {
                    original[dev] = hz;
                    Say(string.Format(CultureInfo.InvariantCulture, "{0}: {1}x{2} @ {3}Hz -> target {4}Hz (offers {5})",
                        dev, w, h, hz, opt.Hz, DisplayMode.Format(DisplayMode.Supported(dev))));
                }
            }
            if (original.Count == 0) { Say("FAIL: no readable display modes"); return 1; }

            // same protection as the daemon: persist before changing
            if (!StateStore.Save(opt.StateHzPath, original, StateStore.KindHz))
            {
                Say("FAIL: could not write " + opt.StateHzPath + "; refusing to change any mode");
                return 1;
            }

            int changed = 0, failed = 0;
            try
            {
                foreach (string dev in devs)
                {
                    if (!original.ContainsKey(dev)) continue;
                    uint target = DisplayMode.PickHz(dev, opt.Hz);
                    if (target == 0) { Say("  " + dev + ": no usable target"); failed++; continue; }
                    if (!DisplayMode.TrySetHz(dev, target)) { Say("  " + dev + ": ChangeDisplaySettingsEx failed"); failed++; continue; }
                    Thread.Sleep(1200);
                    uint now, w, h;
                    DisplayMode.TryGet(dev, out now, out w, out h);
                    Say(string.Format(CultureInfo.InvariantCulture, "  {0}: now {1}x{2} @ {3}Hz", dev, w, h, now));
                    if (now == target) changed++; else failed++;
                }

                string err;
                using (Bitmap shot = Capture.VirtualDesktop(out err))
                {
                    if (shot == null) Say("capture at the new rate FAILED: " + err);
                    else
                    {
                        Say("capture at the new rate: " + shot.Width + "x" + shot.Height
                            + " (desktop was " + Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN) + "x"
                            + Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN) + ")");
                    }
                }
            }
            finally
            {
                foreach (string dev in devs)
                {
                    uint want;
                    if (!original.TryGetValue(dev, out want)) continue;
                    uint now, w, h;
                    if (DisplayMode.TryGet(dev, out now, out w, out h) && now == want) continue;
                    DisplayMode.TrySetHz(dev, want);
                    Thread.Sleep(900);
                }
                bool all = true;
                foreach (string dev in devs)
                {
                    uint want, now, w, h;
                    if (!original.TryGetValue(dev, out want)) continue;
                    DisplayMode.TryGet(dev, out now, out w, out h);
                    Say(string.Format(CultureInfo.InvariantCulture, "restored {0}: {1}Hz (wanted {2}Hz)", dev, now, want));
                    if (now != want) all = false;
                }
                if (all) StateStore.Clear(opt.StateHzPath);
                else Say("refresh-rate record kept for --restore");
                Say(all ? "PASS: every display rolled back to its original rate" : "FAIL: rollback incomplete");
            }
            Say(changed > 0 ? "PASS: " + changed + " display(s) changed rate" : "FAIL: no display changed rate");
            return (failed == 0 && changed > 0) ? 0 : 1;
        }

        // ---- power-plan diagnostics ----
        private static int CmdPlanList()
        {
            Say("== power schemes ==");
            List<KeyValuePair<Guid, string>> all = PowerPlan.List();
            Guid? act = PowerPlan.GetActive();
            foreach (KeyValuePair<Guid, string> kv in all)
                Say((act.HasValue && kv.Key == act.Value ? " * " : "   ") + kv.Key.ToString("D") + "   " + kv.Value);
            Say("active: " + (act.HasValue ? act.Value.ToString("D") : "unknown"));
            Say("aliases: balanced | saver | high | ultimate | <GUID> | <name substring>");
            Say("note: switching the ACTIVE scheme is a per-user setting (PowerSetActiveScheme),");
            Say("      so it needs no administrator rights - verified on this machine.");
            return all.Count > 0 ? 0 : 1;
        }

        // ---- self test ----
        private static int CmdSelftest(Options opt, Log log)
        {
            Say("== ScreenDim self-test ==");
            Say("goal: prove that making the panel physically dark does NOT change what");
            Say("      a screen capture reads from the framebuffer.");

            if (opt.DryRun)
            {
                Say("--dry-run is not meaningful for the self-test: it must really drive the panels.");
                return 2;
            }

            // The self-test writes the panels, so it must not run against a live daemon, and
            // it must never mistake an ALREADY-DARK panel for the original brightness - that
            // would overwrite a real recovery record with 0 and then delete it.
            bool createdNew;
            var probe = new Mutex(true, DaemonMutexName, out createdNew);
            bool daemonAlive = !createdNew;
            if (daemonAlive)
            {
                probe.Close();
                Say("a ScreenDim daemon is running. Stop it first (--stop): the self-test");
                Say("needs to own the panels, and a live daemon's dark state would corrupt it.");
                return 2;
            }
            try
            {
                if (File.Exists(opt.StatePath) || File.Exists(opt.StateHzPath) || File.Exists(opt.StatePlanPath))
                {
                    Say("a recovery record exists: a previous run left the panels dark, the refresh rate");
                    Say("lowered and/or the power scheme downgraded. Run --restore first.");
                    return 2;
                }

                List<PhysMon> mons = Ddc.Enumerate();
                if (mons.Count == 0) { Say("FAIL: no physical monitors"); return 1; }

                var original = new Dictionary<string, uint>();
                var maxByDevice = new Dictionary<string, uint>();
                foreach (PhysMon m in mons)
                {
                    uint cur, max, type;
                    if (Ddc.ReadVcp(m.Handle, Native.VCP_BRIGHTNESS, out cur, out max, out type))
                    {
                        original[m.Device] = cur;
                        maxByDevice[m.Device] = max;
                        Say(string.Format(CultureInfo.InvariantCulture, "monitor {0}: brightness={1}/{2}", m.Device, cur, max));
                    }
                    else Say("monitor " + m.Device + ": DDC read FAILED - will be left untouched");
                }
            if (original.Count == 0)
            {
                Say("FAIL: could not read the brightness of any monitor; refusing to touch the panels");
                foreach (PhysMon m in mons) m.Dispose();
                return 1;
            }

            // --- phase 1: control pair, panels lit -------------------------------
            string err;
            Bitmap lit1 = Capture.VirtualDesktop(out err);
            if (lit1 == null) { Say("FAIL: capture (lit #1): " + err); foreach (PhysMon m in mons) m.Dispose(); return 1; }
            Say("captured lit frame #1: " + lit1.Width + "x" + lit1.Height);
            Thread.Sleep(opt.SelftestSeconds * 1000);
            Bitmap lit2 = Capture.VirtualDesktop(out err);
            if (lit2 == null) { Say("FAIL: capture (lit #2): " + err); lit1.Dispose(); foreach (PhysMon m in mons) m.Dispose(); return 1; }
            Say("captured lit frame #2 after " + opt.SelftestSeconds + "s (control interval)");

            // --- phase 2: drive the panels dark ----------------------------------
            // Persist the restore targets BEFORE touching any panel: a crash, kill or
            // power cut right here is then recovered by the normal startup path.
            if (!StateStore.Save(opt.StatePath, original, StateStore.KindBrightness))
            {
                Say("FAIL: could not write the recovery record " + opt.StatePath);
                Say("      refusing to dim the panels without one.");
                foreach (PhysMon m in mons) m.Dispose();
                return 1;
            }
            Say("dimming panels to " + opt.Level + " ...");
            int applied = 0, changedCount = 0;
            foreach (PhysMon m in mons)
            {
                uint want;
                if (!original.TryGetValue(m.Device, out want))
                {
                    Say("  " + m.Device + " -> skipped (original brightness unknown)");
                    continue;
                }
                uint level = opt.Level;
                uint mx;
                if (maxByDevice.TryGetValue(m.Device, out mx) && mx > 0 && level > mx) level = mx;
                uint rb;
                if (Ddc.WriteVcpVerified(m.Handle, Native.VCP_BRIGHTNESS, level, 3, out rb))
                {
                    applied++;
                    if (rb != want) changedCount++;
                    Say("  " + m.Device + " -> brightness readback " + rb);
                }
                else Say("  " + m.Device + " -> WRITE FAILED");
            }
            Thread.Sleep(1500); // let the panel settle
            Bitmap dark1 = Capture.VirtualDesktop(out err);
            if (dark1 == null) Say("FAIL: capture (dark #1): " + err);
            Thread.Sleep(opt.SelftestSeconds * 1000);
            Bitmap dark2 = Capture.VirtualDesktop(out err);

            // --- phase 3: always restore before reporting ------------------------
            int restored = 0;
            var restorePending = new List<string>();
            foreach (PhysMon m in mons)
            {
                uint want;
                if (!original.TryGetValue(m.Device, out want)) continue;
                uint rb;
                if (Ddc.WriteVcpVerified(m.Handle, Native.VCP_BRIGHTNESS, want, 4, out rb) && rb == want)
                    restored++;
                else
                {
                    restorePending.Add(m.Device);
                    Say("RESTORE PROBLEM on " + m.Device + " (wanted " + want + ", got " + rb + ")");
                }
            }
            foreach (PhysMon m in mons) m.Dispose();
            Say("restored: " + restored + "/" + original.Count);
            if (restorePending.Count == 0) StateStore.Clear(opt.StatePath);
            else Say("recovery record kept; run --restore-saved to retry: " + string.Join(", ", restorePending.ToArray()));

            int rc = 0;
            try
            {
                if (dark1 == null) { Say("FAIL: no dark-state frame captured"); return 1; }

                Directory.CreateDirectory(opt.ShotDir);
                string p1 = Path.Combine(opt.ShotDir, "01-lit.png");
                string p2 = Path.Combine(opt.ShotDir, "02-dark.png");
                lit2.Save(p1, ImageFormat.Png);
                dark1.Save(p2, ImageFormat.Png);
                Say("screenshots: " + p1);
                Say("screenshots: " + p2);

                // (A) is the dark-state capture a real desktop, or a blank frame?
                double mean, std, nonBlack;
                Capture.ContentStats(dark1, out mean, out std, out nonBlack);
                Say(string.Format(CultureInfo.InvariantCulture,
                    "captured-while-dark: meanLuma={0:F1} stdLuma={1:F1} nonBlackPixels={2:F1}%", mean, std, nonBlack));
                bool captureAlive = nonBlack > 5.0 && std > 10.0;

                // (B) witness pixels: static while lit, then the dim happens.
                long staticCount, changedAcross;
                Capture.StaticPixelsChanged(lit1, lit2, dark1, 8, out staticCount, out changedAcross);
                double witnessPct = staticCount == 0 ? 0.0 : 100.0 * changedAcross / staticCount;
                double staticShare = 100.0 * staticCount / ((long)lit1.Width * lit1.Height);
                Say(string.Format(CultureInfo.InvariantCulture,
                    "witness pixels (static across the lit control interval): {0} = {1:F1}% of screen",
                    staticCount, staticShare));
                Say(string.Format(CultureInfo.InvariantCulture,
                    "  of those, changed when the panels went dark         : {0} = {1:F3}%",
                    changedAcross, witnessPct));
                // A PASS here is only meaningful if there is a body of evidence: on a
                // fully animating desktop (full-screen video) there are no static witness
                // pixels, and claiming "untouched" would be a false PASS.
                bool captureIntact;
                int verdict = 0; // 0 = pass, 1 = fail, 2 = inconclusive
                if (staticCount == 0 || staticShare < 1.0)
                {
                    Say(string.Format(CultureInfo.InvariantCulture,
                        "INCONCLUSIVE: only {0:F2}% of the screen stayed still during the control interval -", staticShare));
                    Say("              not enough static evidence to tell whether the dim affected capture.");
                    captureIntact = false;
                    verdict = 2;
                }
                else
                {
                    captureIntact = witnessPct < 5.0;
                    verdict = captureIntact ? 0 : 1;
                }

                // (C) noise floor comparisons - how much of the screen is simply animating
                double mCtl, pCtl, mDark, pDark, mTr, pTr;
                Rectangle bCtl, bDark, bTr;
                Capture.Diff(lit1, lit2, null, out mCtl, out pCtl, out bCtl);
                Capture.Diff(lit2, dark1, null, out mTr, out pTr, out bTr);
                Say(string.Format(CultureInfo.InvariantCulture, "animated share, lit pair (control)  : {0:F3}%", pCtl));
                if (dark2 != null)
                {
                    Capture.Diff(dark1, dark2, null, out mDark, out pDark, out bDark);
                    Say(string.Format(CultureInfo.InvariantCulture, "animated share, dark pair           : {0:F3}%", pDark));
                }
                Say(string.Format(CultureInfo.InvariantCulture,
                    "changed share across the dim step   : {0:F3}%  bounds={1}", pTr, bTr));

                Say(captureAlive
                    ? "PASS: while the panels were dark the capture still held a full desktop image"
                    : "FAIL: capture while dark looks blank - the framebuffer was NOT preserved");

                // A PASS on "capture unaffected" is only meaningful if the panels really
                // changed brightness. With no dim, the dark capture simply equals the lit
                // capture and the witness test would trivially succeed.
                if (changedCount == 0)
                {
                    Say("INCONCLUSIVE: no panel actually changed brightness (requested level equals");
                    Say("              the current value, or no write was accepted) - nothing proven.");
                }
                else if (verdict == 0)
                    Say("PASS: dimming the panel left the captured framebuffer untouched");
                else if (verdict == 1)
                    Say("FAIL: dimming visibly altered the captured framebuffer");

                bool dimTook = applied == original.Count;
                Say(dimTook ? "PASS: every tested monitor accepted the DDC brightness write" : "FAIL: a tested monitor refused the DDC write");
                Say(restored == original.Count ? "PASS: brightness rolled back cleanly" : "FAIL: rollback incomplete");

                if (!captureAlive || verdict == 1) rc = 2;
                if (restored != original.Count) rc = 3;
                if (changedCount == 0 && rc == 0) rc = 4;
                else if (verdict == 2 && rc == 0) rc = 4;
                if (!dimTook && rc == 0) rc = 5;
            }
            finally
            {
                lit1.Dispose();
                lit2.Dispose();
                if (dark1 != null) dark1.Dispose();
                if (dark2 != null) dark2.Dispose();
            }
            return rc;
            }
            finally
            {
                try { probe.ReleaseMutex(); } catch { }
                probe.Close();
            }
        }

        // ---- daemon ----
        private static int CmdDaemon(Options opt, Log log)
        {
            // Two daemons would fight over the backlight, each restoring values from its
            // own view of the world - and each could delete the other's recovery record.
            bool createdNew;
            daemonLock = new Mutex(true, DaemonMutexName, out createdNew);
            if (!createdNew)
            {
                // A daemon is already up. If this invocation was the desktop shortcut,
                // just open the control panel instead of failing silently.
                if (opt.OpenUi) { OpenBrowser(opt.Port, log); return 0; }
                log.W("another ScreenDim daemon already holds " + DaemonMutexName + "; exiting");
                return 2;
            }

            log.W("=== ScreenDim starting: idle=" + opt.IdleSec + "s level=" + opt.Level + " mode=" + opt.Mode
                  + " wake=" + opt.Wake + " threshold=" + opt.ThresholdPx + "px hook=" + (!opt.NoHook)
                  + " hz=" + opt.Hz + " saver-plan=" + (String.IsNullOrEmpty(opt.SaverPlan) ? "off" : opt.SaverPlan) + " ===");

            // Resolve the saver scheme once, up front.
            if (!String.IsNullOrEmpty(opt.SaverPlan))
            {
                Guid g; string nm;
                if (PowerPlan.TryResolve(opt.SaverPlan, out g, out nm))
                {
                    opt.SaverPlanGuid = g;
                    log.W("saver plan resolved: '" + opt.SaverPlan + "' -> " + g.ToString("D") + " (" + nm + ")");
                }
                else
                {
                    log.W("WARNING: cannot resolve saver plan '" + opt.SaverPlan + "' - plan switching disabled");
                    opt.SaverPlan = "";
                }
            }
            Guid? activePlan = PowerPlan.GetActive();
            log.W("active power scheme at startup: " + (activePlan.HasValue ? activePlan.Value.ToString("D") : "unknown"));
            if (activePlan.HasValue && activePlan.Value == opt.SaverPlanGuid && opt.SaverPlanGuid != Guid.Empty)
                log.W("WARNING: the saver plan is already the active one - nothing will change on idle");

            // A control command left over from a previous session must not fire now.
            try { if (File.Exists(opt.CtrlPath)) File.Delete(opt.CtrlPath); } catch { }

            var engine = new Engine(opt, log);
            try { engine.RecoverStrandedState(); }
            catch (Exception ex) { log.W("startup recovery failed: " + ex.GetType().Name + ": " + ex.Message); }
            engine.Start();
            engine.PublishStatus();

            var watcher = new InputWatcher(!opt.NoHook);
            log.W("low-level hooks: mouse=" + watcher.MouseHookInstalled + " keyboard=" + watcher.KeyHookInstalled);
            engine.BindWatcher(watcher);          // so the web UI can report idle seconds
            engine.PeekSec = opt.PeekSec;

            WebUi web = null;
            if (opt.Port > 0)
            {
                web = new WebUi(opt.Port, engine, opt, log);
                if (!web.Start()) web = null;
            }
            else log.W("web ui disabled (--port 0)");
            if (opt.OpenUi) OpenBrowser(opt.Port, log);

            uint mainThreadId = Native.GetCurrentThreadId();

            // Ctrl+C / console close in the CLI build: unwedge the message loop so the
            // normal shutdown path (restore, then wait) runs instead of being killed.
            try
            {
                Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs e)
                {
                    e.Cancel = true;
                    Native.PostThreadMessage(mainThreadId, (uint)Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                };
            }
            catch { }

            // Last-resort fallback if the process is terminated some other way.
            AppDomain.CurrentDomain.ProcessExit += delegate
            {
                engine.RequestAwake();
                for (int i = 0; i < 30 && engine.IsDark; i++) Thread.Sleep(100);
                engine.Stop();
                watcher.Dispose();
            };

            DateTime startUtc = DateTime.UtcNow;
            Native.POINT anchor = InputWatcher.Cursor();
            bool wasDark = false;
            bool wakePending = false;
            long darkSince = InputWatcher.NowMs();
            long lastCtrlWrite = 0;
            DateTime lastCtrlPoll = DateTime.MinValue;
            DateTime lastHookRefresh = DateTime.UtcNow;
            DateTime lastStatus = DateTime.MinValue;

            UIntPtr timerId = Native.SetTimer(IntPtr.Zero, UIntPtr.Zero, (uint)opt.TickMs, IntPtr.Zero);
            if (timerId == UIntPtr.Zero)
            {
                // Without the timer GetMessage would block forever: the daemon could never
                // dim, and --stop / --max-seconds / the control file would all be dead
                // while it still held ES_DISPLAY_REQUIRED.
                log.W("FATAL: SetTimer failed; refusing to run a daemon that can never be controlled");
                engine.RequestAwake();
                for (int i = 0; i < 30 && engine.IsDark; i++) Thread.Sleep(100);
                engine.Stop();
                watcher.Dispose();
                return 1;
            }

            Native.MSG msg;
            while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == Native.WM_QUIT) break;
                if (msg.message != Native.WM_TIMER) { Native.TranslateMessage(ref msg); Native.DispatchMessage(ref msg); continue; }

                // --- control file: polled at most once a second. It is a filesystem stat,
                //     and this loop runs 10x/s for weeks - no need to hammer the disk
                //     (and the hook callbacks are dispatched on this same thread). ---
                if ((DateTime.UtcNow - lastCtrlPoll).TotalSeconds >= 1.0)
                {
                    lastCtrlPoll = DateTime.UtcNow;
                    try
                    {
                        if (File.Exists(opt.CtrlPath) && File.GetLastWriteTimeUtc(opt.CtrlPath).Ticks != lastCtrlWrite)
                        {
                            lastCtrlWrite = File.GetLastWriteTimeUtc(opt.CtrlPath).Ticks;
                            string cmd = File.ReadAllText(opt.CtrlPath, Encoding.UTF8).Trim().ToLowerInvariant();
                            // Consume the command. Leaving it behind would make the next
                            // daemon instance re-run it at startup - a stale "stop" would
                            // kill the freshly autostarted daemon.
                            try { File.Delete(opt.CtrlPath); } catch { }
                            if (cmd.StartsWith("dim")) { engine.RequestDark(); log.W("control: dim"); }
                            else if (cmd.StartsWith("wake")) { engine.RequestAwake(); log.W("control: wake"); }
                            else if (cmd.StartsWith("stop")) { log.W("control: stop"); break; }
                        }
                    }
                    catch { }
                }

                // --- self-heal: Windows silently drops a low-level hook whose callback
                //     misses the LowLevelHooksTimeout window, so re-arm it periodically ---
                if ((DateTime.UtcNow - lastHookRefresh).TotalMinutes >= 30)
                {
                    lastHookRefresh = DateTime.UtcNow;
                    watcher.Reinstall();
                    log.V("hooks re-armed: mouse=" + watcher.MouseHookInstalled + " keyboard=" + watcher.KeyHookInstalled);
                }

                // --- pointer movement: net displacement per tick, jitter and
                //     polling-rate independent (see InputWatcher notes) ---
                long nowTick = InputWatcher.NowMs();
                Native.POINT now = InputWatcher.Cursor();
                if (InputWatcher.Distance(now, anchor) > opt.ThresholdPx)
                {
                    watcher.MarkMoved();
                    anchor = now;
                }

                // --- state transitions first, so the activity windows are reset
                //     before the wake test reads them ---
                if (engine.IsDark != wasDark)
                {
                    wasDark = engine.IsDark;
                    wakePending = false;
                    long t = nowTick;
                    watcher.Reset(t);              // stale pre-transition events must not fire
                    anchor = InputWatcher.Cursor();
                    darkSince = t;
                    log.W("[state] " + (wasDark ? "panels dark (framebuffer still live)" : "panels lit"));
                }

                // --- idle / wake decision ---
                // Four states:
                //   brightness-lock ON   -> backlight-only dim, MOUSE IS IGNORED entirely;
                //                           only a keypress clears it (blind escape hatch)
                //   manual ON, peek>0    -> dark after PeekSec of no input (mouse peeks)
                //   manual ON, peek==0   -> same as brightness-lock: latched, mouse ignored
                //   auto ON              -> dark after --idle seconds
                //   all off              -> never darken on its own
                bool latched = engine.BrightnessLock || (engine.ManualMode && engine.PeekSec <= 0);
                long timeoutMs;
                if (latched) timeoutMs = 0;
                else if (engine.ManualMode) timeoutMs = (long)engine.PeekSec * 1000L;
                else if (engine.AutoEnabled) timeoutMs = (long)opt.IdleSec * 1000L;
                else timeoutMs = long.MaxValue;

                if (!engine.IsDark)
                {
                    long idle = watcher.IdleMs();
                    if (idle >= timeoutMs)
                        engine.RequestDark(engine.BrightnessLock && opt.LockBrightnessOnly);
                }
                else if (!wakePending)
                {
                    if (latched)
                    {
                        // Deliberately nothing wakes this mode - not the mouse, not the
                        // keyboard. On panels whose backlight still glows at VCP 0 the
                        // content stays readable, so the panel button is always reachable.
                        // Out-of-band escapes that need no eyesight: --wake-now / --stop.
                        if (opt.Verbose && (InputWatcher.NowMs() - darkSince) % 10000 < 120)
                            log.V("latched: ignoring input (mouse/keyboard)");
                    }
                    else
                    {
                        bool wakeMouse = watcher.MouseActivitySince(darkSince);
                        bool wakeKey = opt.Wake == "any" && watcher.KeyActivitySince(darkSince);
                        if (wakeMouse || wakeKey)
                        {
                            log.W("wake trigger mouse=" + wakeMouse + " keyboard=" + wakeKey
                                  + " (idle " + (watcher.IdleMs() / 1000) + "s, dark for "
                                  + ((InputWatcher.NowMs() - darkSince) / 1000) + "s)");
                            engine.RequestAwake();
                            wakePending = true;
                        }
                    }
                }

                if ((DateTime.UtcNow - lastStatus).TotalSeconds >= 30)
                {
                    lastStatus = DateTime.UtcNow;
                    log.V("heartbeat idle=" + (watcher.IdleMs() / 1000) + "s sysIdle="
                          + (watcher.SystemIdleMs() / 1000) + "s dark=" + engine.IsDark);
                }

                if (opt.MaxSeconds > 0 && (DateTime.UtcNow - startUtc).TotalSeconds >= opt.MaxSeconds)
                {
                    log.W("max-seconds reached, exiting");
                    break;
                }

                // Watchdog: a dead engine thread means the panels may still be dark and
                // the execution-state hold is gone. Better to exit (and let the next
                // start recover from state.txt) than to pretend to be healthy.
                if (!engine.Alive)
                {
                    log.W("engine thread is gone - exiting so the next start can recover");
                    break;
                }
            }

            Native.KillTimer(IntPtr.Zero, timerId);
            // Wait for the engine to finish its DDC write-back. Exiting 400 ms after
            // asking for a wake-up could cut a ~0.5 s restore in half and strand a
            // panel at brightness 0 until the next start.
            engine.RequestAwake();
            for (int i = 0; i < 150 && engine.IsDark; i++) Thread.Sleep(100);
            engine.Stop();
            bool restoreOk = !engine.IsDark;
            watcher.Dispose();
            if (web != null) web.Stop();
            log.W("=== ScreenDim stopped ===" + (restoreOk ? "" : " (restore INCOMPLETE - state.txt kept)"));
            return restoreOk ? 0 : 1;
        }

        /// <summary>Open the control panel, waiting briefly for the daemon to start listening.</summary>
        private static void OpenBrowser(int port, Log log)
        {
            if (port <= 0) return;
            for (int i = 0; i < 40; i++)
            {
                try
                {
                    using (var c = new TcpClient())
                    {
                        c.Connect(IPAddress.Loopback, port);
                        break;
                    }
                }
                catch { Thread.Sleep(150); }
            }
            try
            {
                Process.Start(new ProcessStartInfo("http://127.0.0.1:" + port + "/") { UseShellExecute = true });
                log.W("opened control panel at http://127.0.0.1:" + port + "/");
            }
            catch (Exception ex) { log.W("could not open the browser: " + ex.Message); }
        }

        private static void PrintHelp()
        {
            string t = @"
ScreenDim - darken the physical panel via DDC/CI while keeping the framebuffer alive
so that screen capture and desktop-recognition keep working.

USAGE
  ScreenDim.exe --daemon [options]        run the idle-darkening daemon
  ScreenDim.exe --daemon --open-ui        ...and open the control panel in the browser
  ScreenDimCLI.exe --list                 show monitors + DDC capabilities
  ScreenDimCLI.exe --hz-list              show refresh rates each display offers
  ScreenDimCLI.exe --plan-list            show power schemes + the active one
  ScreenDimCLI.exe --hz-test              change rate, verify capture, roll back
  ScreenDimCLI.exe --selftest             dim briefly and prove capture is unaffected
  ScreenDimCLI.exe --status               show daemon state
  ScreenDimCLI.exe --dim-now              ask a running daemon to darken now
  ScreenDimCLI.exe --wake-now             ask a running daemon to light up now
  ScreenDimCLI.exe --restore-saved        undo a dark state left by a crash
  ScreenDimCLI.exe --stop                 ask a running daemon to exit

OPTIONS
  --idle <sec>          idle time before darkening              (default 60)
  --level <0-100>       panel brightness while dark              (default 0)
  --hz <n>              refresh rate while dark                  (default 60, 0=off)
  --no-hz               do not touch the refresh rate
  --saver-plan <p>      power plan while dark: balanced|saver|high|<GUID>|<name> (default balanced)
  --no-plan             do not touch the power plan
  --mode dim|off        dim backlight, or DDC power-off          (default dim)
  --allow-power-off     required to let --mode off run
  --wake mouse|any      what wakes the panels; any = keyboard too (default mouse)
  --threshold <px>      cursor travel counted as movement        (default 4)
  --reassert <sec>      re-assert the dark level                 (default 20, 0=off)
  --restore-level <n>   fallback brightness if a read fails      (default 70)
  --allow-sleep         do not hold the display/system awake
  --no-hook             do not install the low-level mouse/keyboard hooks
  --tick <ms>           poll interval                            (default 100)
  --port <n>            web control panel port on 127.0.0.1        (default 8765, 0=off)
  --peek <sec>          manual mode: seconds lit after input       (default 15, 0=latch)
  --lock-level <n>      brightness-only lock level, 0-100          (default 0)
  --open-ui             open http://127.0.0.1:<port>/ in the browser
  --dry-run             log decisions without writing to monitors
  --verbose             verbose log
  --max-seconds <n>     exit after n seconds (testing)
  --log/--state/--ctrl/--status-file/--shots <path>

FILES  (defaults under %LOCALAPPDATA%\ScreenDim)
  screendim.log   report.txt   state.txt   status.txt   shots\
";
            try { Console.WriteLine(t); } catch { }
            if (reportPath != null)
            {
                try { File.WriteAllText(reportPath, t, Encoding.UTF8); } catch { }
            }
        }
    }
}
