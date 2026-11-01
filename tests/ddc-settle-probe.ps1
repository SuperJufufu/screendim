# How long is the DDC/CI channel unavailable after a refresh-rate change?
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Collections.Generic; using System.Diagnostics;
public class PR {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
  public struct DEVMODE { [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmDeviceName;
    public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public uint dmFields;
    public int dmPositionX, dmPositionY; public uint dmDisplayOrientation, dmDisplayFixedOutput;
    public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmFormName; public ushort dmLogPixels;
    public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
    public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight; }
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool EnumDisplaySettings(string d, int m, ref DEVMODE dm);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int ChangeDisplaySettingsEx(string d, ref DEVMODE dm, IntPtr h, uint f, IntPtr p);
  [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr c, MonEnum cb, IntPtr d);
  public delegate bool MonEnum(IntPtr h, IntPtr hdc, ref RECT r, IntPtr d);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int l,t,r,b; }
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct PM { public IntPtr h; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string desc; }
  [DllImport("dxva2.dll", ExactSpelling=true)] public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr h, out uint n);
  [DllImport("dxva2.dll", ExactSpelling=true)] public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr h, uint n, [Out] PM[] a);
  [DllImport("dxva2.dll", ExactSpelling=true)] public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte c, out uint t, out uint cur, out uint mx);
  public static DEVMODE Get(string d){ var m=new DEVMODE(); m.dmSize=(ushort)Marshal.SizeOf(typeof(DEVMODE)); EnumDisplaySettings(d,-1,ref m); return m; }
  public static bool SetHz(string d, uint hz){ var m=Get(d); if(m.dmDisplayFrequency==hz) return true; m.dmDisplayFrequency=hz; m.dmFields=0x40000|0x80000|0x100000|0x400000; return ChangeDisplaySettingsEx(d, ref m, IntPtr.Zero, 0, IntPtr.Zero)==0; }
  public static long DdcProbe(int hmonIdx){
    var hs=new List<IntPtr>();
    EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr h, IntPtr hdc, ref RECT r, IntPtr d){ hs.Add(h); return true; }, IntPtr.Zero);
    if (hmonIdx >= hs.Count) return -1;
    uint n; if(!GetNumberOfPhysicalMonitorsFromHMONITOR(hs[hmonIdx], out n)) return -1;
    var a=new PM[n]; if(!GetPhysicalMonitorsFromHMONITOR(hs[hmonIdx], n, a)) return -1;
    var sw=Stopwatch.StartNew();
    for(int i=0;i<200;i++){
      uint t,c,m;
      if(GetVCPFeatureAndVCPFeatureReply(a[0].h, 0x10, out t, out c, out m)) return sw.ElapsedMilliseconds;
      System.Threading.Thread.Sleep(50);
    }
    return -1;
  }
}
'@

$sw = [System.Diagnostics.Stopwatch]::new()
$d2 = '\.\DISPLAY2'
foreach ($dev in @('\\.\DISPLAY1','\\.\DISPLAY2')) {
  $cur = [PR]::Get($dev)
  $other = if ($cur.dmDisplayFrequency -gt 100) { 60 } else { $cur.dmDisplayFrequency }
  "--- $dev : $($cur.dmDisplayFrequency)Hz -> 60Hz -> back ---"
  [void][PR]::SetHz($dev, 60); Start-Sleep -Seconds 3
  $sw.Restart()
  [void][PR]::SetHz($dev, $cur.dmDisplayFrequency)
  $t = $sw.ElapsedMilliseconds
  "  ChangeDisplaySettingsEx returned after $t ms"
  $sw.Restart()
  $ms = [PR]::DdcProbe(0)
  "  first successful DDC read after $($sw.ElapsedMilliseconds) ms (probe said $ms ms)"
  Start-Sleep -Seconds 2
}
foreach ($dev in @('\\.\DISPLAY1','\\.\DISPLAY2')) { $c=[PR]::Get($dev); "final $dev = $($c.dmDisplayFrequency)Hz" }
