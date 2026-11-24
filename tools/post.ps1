# Dot-source: posts mouse messages straight to FluxTube's video surface (no real pointer, no focus change).
# Coordinates are video-surface pixels. Use with a FluxTube started with --test.
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public static class FluxPost {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  public static IntPtr Find(IntPtr top, string cls) { IntPtr f = IntPtr.Zero; EnumChildWindows(top, (h,l) => { var c = new StringBuilder(128); GetClassName(h,c,128); if (c.ToString()==cls) { f=h; return false; } return true; }, IntPtr.Zero); return f; }
}
"@ -ErrorAction SilentlyContinue
function Get-FluxHost([string]$Match = "*") {
    $p = Get-Process FluxTube | Where-Object { $_.Path -like $Match } | Select-Object -First 1
    [FluxPost]::Find($p.MainWindowHandle, "FluxVideoHost")
}
function Send-FluxMove($h, [int]$x, [int]$y) { [FluxPost]::PostMessage($h, 0x200, [IntPtr]0, [IntPtr]($x + ($y -shl 16))) | Out-Null }
function Send-FluxClick($h, [int]$x, [int]$y) {
    $lp = [IntPtr]($x + ($y -shl 16))
    [FluxPost]::PostMessage($h, 0x200, [IntPtr]0, $lp) | Out-Null; Start-Sleep -Milliseconds 150
    [FluxPost]::PostMessage($h, 0x201, [IntPtr]1, $lp) | Out-Null; Start-Sleep -Milliseconds 70
    [FluxPost]::PostMessage($h, 0x202, [IntPtr]0, $lp) | Out-Null; Start-Sleep -Milliseconds 600
}
function Send-FluxWheel($h, [int]$notches) { [FluxPost]::PostMessage($h, 0x20A, [IntPtr](($notches * 120) -shl 16), [IntPtr]0) | Out-Null }
