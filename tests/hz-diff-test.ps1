# Capture-path integrity across a refresh-rate change and a DDC backlight-off,
# measured as the mean luma of the DIFFERENCE frame (0 = identical captures).
$ErrorActionPreference = 'Stop'
$ff = 'C:\Users\Administrator\AppData\Local\Microsoft\WinGet\Links\ffmpeg.exe'

Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Collections.Generic;
public class CY {
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
  [DllImport("dxva2.dll", ExactSpelling=true)] public static extern bool SetVCPFeature(IntPtr h, byte c, uint v);
  [DllImport("dxva2.dll", ExactSpelling=true)] public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte c, out uint t, out uint cur, out uint mx);
  public static DEVMODE Get(string d){ var m=new DEVMODE(); m.dmSize=(ushort)Marshal.SizeOf(typeof(DEVMODE)); EnumDisplaySettings(d,-1,ref m); return m; }
  public static int SetHz(string d, uint hz){ var m=Get(d); m.dmDisplayFrequency=hz; m.dmFields=0x40000|0x80000|0x100000|0x400000; return ChangeDisplaySettingsEx(d, ref m, IntPtr.Zero, 0, IntPtr.Zero); }
  public static List<string> Devices(){ var l=new List<string>();
    EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr h, IntPtr hdc, ref RECT r, IntPtr d){
      var mi=new MIEX(); mi.cbSize=Marshal.SizeOf(typeof(MIEX)); if(GetMonitorInfo(h,ref mi)) l.Add(mi.szDevice); return true; }, IntPtr.Zero);
    return l; }
  public static List<IntPtr> Phys(){ var l=new List<IntPtr>(); var hs=new List<IntPtr>();
    EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr h, IntPtr hdc, ref RECT r, IntPtr d){ hs.Add(h); return true; }, IntPtr.Zero);
    foreach(var h in hs){ uint n; if(!GetNumberOfPhysicalMonitorsFromHMONITOR(h,out n)) continue;
      var a=new PM[n]; if(!GetPhysicalMonitorsFromHMONITOR(h,n,a)) continue; foreach(var p in a) l.Add(p.h); }
    return l; }
  public static uint Bright(IntPtr h){ uint t,c,m; GetVCPFeatureAndVCPFeatureReply(h,0x10,out t,out c,out m); return c; }
  public static void SetBright(IntPtr h, uint v){ SetVCPFeature(h,0x10,v); }
}
'@

$devs = [CY]::Devices()
$origHz = @{}
foreach ($d in $devs) { $origHz[$d] = [CY]::Get($d).dmDisplayFrequency }
$h0 = [CY]::Phys(); $origBr = @(); foreach ($h in $h0) { $origBr += [CY]::Bright($h) }
"original: $(($devs | ForEach-Object { "$_=$($origHz[$_])Hz" }) -join '  ')  brightness=$($origBr -join '/')`n"

$t = "$env:TEMP\hz2"
function Grab([string]$name) {
  $p = "$t-$name.png"
  Remove-Item $p -ErrorAction SilentlyContinue
  & $ff -hide_banner -loglevel error -f lavfi -i "ddagrab=output_idx=0,hwdownload,format=bgra" -frames:v 1 -update 1 -y $p 2>$null
  return $p
}
function DiffY([string]$a, [string]$b, [string]$label) {
  $stat = "$t-diff.txt"
  Remove-Item $stat -ErrorAction SilentlyContinue
  & $ff -hide_banner -loglevel error -i $a -i $b -filter_complex "[0:v][1:v]blend=all_mode=difference,signalstats,metadata=print:file=$stat" -f null - 2>$null
  $y = 'n/a'
  if (Test-Path $stat) {
    $line = Get-Content $stat | Select-String 'YAVG' | Select-Object -First 1
    if ($line) { $y = (($line.ToString() -split '=')[-1]).Trim() }
  }
  "{0,-46} diff-frame mean luma = {1}" -f $label, $y
}

try {
  # control: two captures at the SAME settings -> the desktop's own churn
  $x1 = Grab 'x1'; Start-Sleep -Seconds 3; $x2 = Grab 'x2'
  DiffY $x1 $x2 "CONTROL  highHz/bright vs highHz/bright (3s apart)"

  foreach ($d in $devs) { [void][CY]::SetHz($d, 60) }
  Start-Sleep -Seconds 5
  $y1 = Grab 'y1'
  DiffY $x2 $y1 "highHz/bright   -> 60Hz/bright"

  $h1 = [CY]::Phys(); foreach ($h in $h1) { [CY]::SetBright($h, 0) }
  Start-Sleep -Seconds 3
  $z1 = Grab 'z1'
  DiffY $y1 $z1 "60Hz/bright     -> 60Hz/BACKLIGHT-OFF"

  $z2 = Grab 'z2'
  DiffY $z1 $z2 "60Hz/dark       vs 60Hz/dark (3s apart)"

  $info = (& $ff -hide_banner -i $z1 2>&1 | Select-String 'Video:').ToString()
  "`nframe captured while dark+60Hz: $($info -replace '.*Video: ','')"
  "file sizes: A(x2)=$([math]::Round((Get-Item $x2).Length/1KB))KB  B(y1)=$([math]::Round((Get-Item $y1).Length/1KB))KB  C(z1)=$([math]::Round((Get-Item $z1).Length/1KB))KB"
}
finally {
  foreach ($d in $devs) { [void][CY]::SetHz($d, $origHz[$d]) }
  Start-Sleep -Seconds 5
  $h2 = [CY]::Phys()
  for ($i = 0; $i -lt $h2.Count -and $i -lt $origBr.Count; $i++) { [CY]::SetBright($h2[$i], $origBr[$i]) }
  Start-Sleep -Seconds 2
  $h3 = [CY]::Phys()
  "`nrestored: $(($devs | ForEach-Object { "$_=$([CY]::Get($_).dmDisplayFrequency)Hz" }) -join '  ')  brightness=$(($h3 | ForEach-Object { [CY]::Bright($_) }) -join '/')"
}
