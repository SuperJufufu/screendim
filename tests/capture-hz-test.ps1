# Isolate the effect of (1) lowering refresh rate and (2) DDC backlight off
# on BOTH the GDI and the DXGI Desktop Duplication capture paths.
$ErrorActionPreference = 'Stop'
$ff = 'C:\Users\Administrator\AppData\Local\Microsoft\WinGet\Links\ffmpeg.exe'

Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Collections.Generic;
public class CX {
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
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct MIEX { public int cbSize; public RECT rcM, rcW; public uint f; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string szDevice; }
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr h, ref MIEX mi);
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct PM { public IntPtr h; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string desc; }
  [DllImport("dxva2.dll", ExactSpelling=true)] public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr h, out uint n);
  [DllImport("dxva2.dll", ExactSpelling=true)] public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr h, uint n, [Out] PM[] a);
  [DllImport("dxva2.dll", ExactSpelling=true)] public static extern bool DestroyPhysicalMonitors(uint n, [In] PM[] a);
  [DllImport("dxva2.dll", ExactSpelling=true)] public static extern bool SetVCPFeature(IntPtr h, byte c, uint v);
  [DllImport("dxva2.dll", ExactSpelling=true)] public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte c, out uint t, out uint cur, out uint mx);
  public static DEVMODE Get(string d){ var m=new DEVMODE(); m.dmSize=(ushort)Marshal.SizeOf(typeof(DEVMODE)); EnumDisplaySettings(d,-1,ref m); return m; }
  public static int SetHz(string d, uint hz){ var m=Get(d); m.dmDisplayFrequency=hz; m.dmFields=0x40000|0x80000|0x100000|0x400000; return ChangeDisplaySettingsEx(d, ref m, IntPtr.Zero, 0, IntPtr.Zero); }
  public static List<string> Devices(){ var l=new List<string>();
    EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr h, IntPtr hdc, ref RECT r, IntPtr d){
      var mi=new MIEX(); mi.cbSize=Marshal.SizeOf(typeof(MIEX)); if(GetMonitorInfo(h,ref mi)) l.Add(mi.szDevice); return true; }, IntPtr.Zero);
    return l; }
  public static List<IntPtr> Phys(){ var l=new List<IntPtr>();
    foreach(var h in new List<IntPtr>()) {}
    var hs=new List<IntPtr>();
    EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr h, IntPtr hdc, ref RECT r, IntPtr d){ hs.Add(h); return true; }, IntPtr.Zero);
    foreach(var h in hs){ uint n; if(!GetNumberOfPhysicalMonitorsFromHMONITOR(h,out n)) continue;
      var a=new PM[n]; if(!GetPhysicalMonitorsFromHMONITOR(h,n,a)) continue; foreach(var p in a) l.Add(p.h); }
    return l; }
  public static uint Bright(IntPtr h){ uint t,c,m; GetVCPFeatureAndVCPFeatureReply(h,0x10,out t,out c,out m); return c; }
  public static void SetBright(IntPtr h, uint v){ SetVCPFeature(h,0x10,v); }
}
'@

$devs = [CX]::Devices()
$origHz = @{}
foreach ($d in $devs) { $origHz[$d] = [CX]::Get($d).dmDisplayFrequency }

$h0 = [CX]::Phys()
$origBr = @()
foreach ($h in $h0) { $origBr += [CX]::Bright($h) }

"original: " + (($devs | ForEach-Object { "$_=$($origHz[$_])Hz" }) -join '  ') + "   brightness=$($origBr -join '/')"
""

function Snap([string]$path, [string]$tag) {
  Remove-Item $path -ErrorAction SilentlyContinue
  & $ff -hide_banner -loglevel error -f lavfi -i "ddagrab=output_idx=0,hwdownload,format=bgra" -frames:v 1 -update 1 -y $path 2>$null
  $info = (& $ff -hide_banner -i $path 2>&1 | Select-String 'Video:').ToString()
  $res = if ($info -match '(\d+x\d+)') { $matches[1] } else { '?' }
  $y = ''
  $s = & $ff -hide_banner -loglevel error -i $path -vf "signalstats,metadata=print:key=lavfi.signalstats.YAVG" -f null - 2>&1
  $line = ($s | Select-String 'YAVG' | Select-Object -First 1)
  if ($line) { $y = ($line.ToString() -split '=')[-1].Trim() }
  $kb = [math]::Round((Get-Item $path).Length / 1KB)
  "{0,-34} res={1,-10} YAVG={2,-8} {3} KB" -f $tag, $res, $y, $kb
}

$t = "$env:TEMP\sd"
try {
  "=== phase A: high refresh, backlight normal (baseline) ==="
  Snap "$t-A.png" "A  $($origHz[$devs[0]])/$($origHz[$devs[1]])Hz  bright"

  "`n=== phase B: 60Hz, backlight normal (isolates refresh change only) ==="
  foreach ($d in $devs) { [void][CX]::SetHz($d, 60) }
  Start-Sleep -Seconds 4
  Snap "$t-B.png" "B  60/60Hz  bright"

  "`n=== phase C: 60Hz + DDC backlight 0 (full feature state) ==="
  $h1 = [CX]::Phys()
  foreach ($h in $h1) { [CX]::SetBright($h, 0) }
  Start-Sleep -Seconds 3
  $rb = ($h1 | ForEach-Object { [CX]::Bright($_) }) -join '/'
  "   ddc brightness readback = $rb"
  Snap "$t-C.png" "C  60/60Hz  dark"
}
finally {
  "`nrestoring ..."
  foreach ($d in $devs) { [void][CX]::SetHz($d, $origHz[$d]) }
  Start-Sleep -Seconds 4
  # handles may be invalid after a mode change - re-enumerate, then restore brightness
  $h2 = [CX]::Phys()
  for ($i = 0; $i -lt $h2.Count -and $i -lt $origBr.Count; $i++) { [CX]::SetBright($h2[$i], $origBr[$i]) }
  Start-Sleep -Seconds 2
  $h3 = [CX]::Phys()
  "final: " + (($devs | ForEach-Object { "$_=$([CX]::Get($_).dmDisplayFrequency)Hz" }) -join '  ') +
           "   brightness=$(($h3 | ForEach-Object { [CX]::Bright($_) }) -join '/')"
}

"`n=== capture comparison ==="
foreach ($p in 'A','B') {
  $x = & $ff -hide_banner -loglevel error -i "$t-A.png" -i "$t-$p.png" -lavfi "[0:v][1:v]ssim" -f null - 2>&1
  $v = ($x | Select-String 'SSIM') 
  "A vs $p : $($v.ToString().Trim())"
}
$x = & $ff -hide_banner -loglevel error -i "$t-A.png" -i "$t-C.png" -lavfi "[0:v][1:v]ssim" -f null - 2>&1
"$((($x | Select-String 'SSIM')).ToString().Trim().Replace('A vs','A vs C (dimmed+60Hz)'))"
