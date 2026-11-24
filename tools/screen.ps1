# Copies the FluxTube window area from the screen (includes the mpv video layer, unlike PrintWindow). No input is sent.
param([string]$Out = "$env:TEMP\fluxtube-screen.png", [string]$Match = "*")
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class S {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
[S]::SetProcessDpiAwarenessContext([IntPtr]-4) | Out-Null
$p = Get-Process FluxTube -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 -and $_.Path -like $Match } | Select-Object -First 1
$r = New-Object S+RECT
[S]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
$w = $r.R - $r.L; $h = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $h))
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
"$Out ${w}x$h"
