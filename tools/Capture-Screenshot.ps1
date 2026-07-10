$ErrorActionPreference = 'Stop'
$exe = "$env:LOCALAPPDATA\Programs\SonarMiniMixer\SonarMiniMixer.exe"
$cli = "$env:LOCALAPPDATA\Programs\SonarMiniMixer\SonarMiniMixer.Cli.exe"
$project = Split-Path -Parent $PSScriptRoot
$out = Join-Path $project 'docs\images\sonar-mini-mixer.png'
New-Item -ItemType Directory -Force (Split-Path -Parent $out) | Out-Null
Get-Process SonarMiniMixer -ErrorAction SilentlyContinue | Stop-Process -Force
$p = Start-Process $exe -PassThru
Start-Sleep -Seconds 3
& $cli show | Out-Null
Start-Sleep -Seconds 2
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class CaptureWindow {
  public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
  public struct RECT { public int Left, Top, Right, Bottom; }
  public static IntPtr Find(uint target) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((hwnd, _) => { uint processId; GetWindowThreadProcessId(hwnd, out processId); if (processId == target && IsWindowVisible(hwnd)) { found = hwnd; return false; } return true; }, IntPtr.Zero);
    return found;
  }
}
'@
$hwnd = [CaptureWindow]::Find([uint32]$p.Id)
if ($hwnd -eq [IntPtr]::Zero) { throw 'No visible app window found.' }
$rect = New-Object CaptureWindow+RECT
[CaptureWindow]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$bitmap = New-Object Drawing.Bitmap ($rect.Right-$rect.Left),($rect.Bottom-$rect.Top)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$hdc = $graphics.GetHdc()
try { [CaptureWindow]::PrintWindow($hwnd, $hdc, 2) | Out-Null } finally { $graphics.ReleaseHdc($hdc); $graphics.Dispose() }
$bitmap.Save($out, [Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
& $cli exit | Out-Null
Write-Output $out
