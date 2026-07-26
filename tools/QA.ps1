$ErrorActionPreference = 'Stop'
$exe = "$env:LOCALAPPDATA\Programs\SonarMiniMixer\SonarMiniMixer.exe"
$cli = "$env:LOCALAPPDATA\Programs\SonarMiniMixer\SonarMiniMixer.Cli.exe"
$result = [System.Collections.Generic.List[string]]::new()
function Check([string]$name, [bool]$ok, [string]$detail) {
  $result.Add("$($(if($ok){'PASS'}else{'FAIL'})) | $name | $detail")
}

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WindowProbe {
  public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
  public static IntPtr FindVisible(uint target) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((hwnd, _) => {
      uint processId;
      GetWindowThreadProcessId(hwnd, out processId);
      if (processId == target && IsWindowVisible(hwnd)) { found = hwnd; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
'@

Get-Process SonarMiniMixer -ErrorAction SilentlyContinue | Stop-Process -Force
$p = Start-Process $exe -PassThru
$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 30 -and $hwnd -eq [IntPtr]::Zero; $i++) {
  Start-Sleep -Milliseconds 250
  $hwnd = [WindowProbe]::FindVisible([uint32]$p.Id)
}
Check 'Process starts' (-not $p.HasExited) "PID=$($p.Id)"
Check 'Main window created' ($hwnd -ne [IntPtr]::Zero) "HWND=$hwnd"
if ($hwnd -eq [IntPtr]::Zero) { $result; exit 1 }

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
Check 'UI Automation root' ($null -ne $root) "name=$($root.Current.Name)"
$sliders = @()
$selectors = @()
$controlButtons = @()
$unnamedControlButtons = @()
for ($i = 0; $i -lt 20; $i++) {
  $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  $sliders = @($all | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Slider })
  $selectors = @($all | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::ComboBox })
  $buttons = @($all | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button })
  $controlButtons = @($buttons | Where-Object { $_.Current.IsControlElement })
  $unnamedControlButtons = @($controlButtons | Where-Object { [string]::IsNullOrWhiteSpace($_.Current.Name) })
  if ($sliders.Count -eq 7 -and $selectors.Count -eq 11 -and $controlButtons.Count -ge 10) { break }
  Start-Sleep -Milliseconds 250
}
Check 'Seven mixer sliders visible' ($sliders.Count -eq 7) "count=$($sliders.Count) names=$((@($sliders)|ForEach-Object{$_.Current.Name}) -join ',')"
Check 'Eleven channel selectors visible' ($selectors.Count -eq 11) "count=$($selectors.Count) names=$((@($selectors)|ForEach-Object{$_.Current.Name}) -join ',')"
Check 'Mixer buttons have accessible names' ($controlButtons.Count -ge 10 -and $unnamedControlButtons.Count -eq 0) "control=$($controlButtons.Count) unnamed=$($unnamedControlButtons.Count) names=$((@($controlButtons)|ForEach-Object{$_.Current.Name}) -join ',')"
$rect = $root.Current.BoundingRectangle
Check 'Compact window bounds' ($rect.Width -ge 640 -and $rect.Width -le 1180 -and $rect.Height -ge 372 -and $rect.Height -le 650) "rect=$([math]::Round($rect.Width))x$([math]::Round($rect.Height))"
Check 'Window is onscreen' (-not $root.Current.IsOffscreen) "offscreen=$($root.Current.IsOffscreen)"

Start-Sleep -Seconds 10
$p.Refresh()
$c1 = $p.TotalProcessorTime
Start-Sleep -Seconds 10
$p.Refresh()
$cpu = (($p.TotalProcessorTime-$c1).TotalMilliseconds / 10000) * 100 / [Environment]::ProcessorCount
Check 'Idle CPU under 1 percent' ($cpu -lt 1) "cpu=$([math]::Round($cpu,3))%"
Check 'Working set under 220 MB' ($p.WorkingSet64 -lt 220MB) "rss=$([math]::Round($p.WorkingSet64/1MB,1))MB"

& $cli exit | Out-Null
Start-Sleep -Seconds 2
$alive = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
Check 'IPC exit closes app' ($null -eq $alive) "alive=$($null -ne $alive)"

$result | Set-Content "$env:TEMP\SonarMiniMixer-QA.txt"
$result
if ($result.Where({$_.StartsWith('FAIL')}).Count -gt 0) { exit 1 }
