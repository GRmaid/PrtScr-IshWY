param([string]$ProcessName = "IshWY")

Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class W32Script {
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lp);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
}
'@

$procs = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue
if (-not $procs) {
  Write-Output "NO_PROCESS"
  exit
}

foreach ($p in $procs) {
  $target = $p.Id
  $cb = [W32Script+EnumWindowsProc]{
    param($h, $lp)
    [uint32]$wpid = 0
    [W32Script]::GetWindowThreadProcessId($h, [ref]$wpid) | Out-Null
    if ($wpid -eq $target -and [W32Script]::IsWindowVisible($h)) {
      $sb = New-Object System.Text.StringBuilder 256
      [W32Script]::GetWindowText($h, $sb, 256) | Out-Null
      Write-Output ("VIS hwnd=" + $h + " title='" + $sb.ToString() + "'")
    }
    return $true
  }
  [W32Script]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
}
