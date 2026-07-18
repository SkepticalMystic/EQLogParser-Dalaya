# PowerShell helpers for driving the live EQLogParser-Dalaya.exe (mouse/keyboard/screenshot)
# to capture real in-app screenshots for the website guides. See ../../tools/guide-screenshots/README.md.
#
# PowerShell tool calls in this environment don't persist state between invocations, so
# dot-source this file at the top of every PowerShell block that needs it:
#   . "<path-to-this-file>\ui-helpers.ps1"

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class UiHelperWin32 {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
  [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
  public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
  public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
  public const uint MOUSEEVENTF_LEFTUP = 0x0004;
  public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
  public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
  public const byte VK_CONTROL = 0x11;
  public const uint KEYEVENTF_KEYUP = 0x0002;
}
"@

function Get-AppRect($procId) {
  $hwnd = (Get-Process -Id $procId).MainWindowHandle
  $rect = New-Object UiHelperWin32+RECT
  [UiHelperWin32]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
  return $rect
}

function Focus-App($procId) {
  $hwnd = (Get-Process -Id $procId).MainWindowHandle
  [UiHelperWin32]::SetForegroundWindow($hwnd) | Out-Null
  Start-Sleep -Milliseconds 200
}

function Click-At($x, $y, $delayAfter = 400) {
  [UiHelperWin32]::SetCursorPos($x, $y)
  Start-Sleep -Milliseconds 120
  [UiHelperWin32]::mouse_event([UiHelperWin32]::MOUSEEVENTF_LEFTDOWN,0,0,0,[UIntPtr]::Zero)
  Start-Sleep -Milliseconds 60
  [UiHelperWin32]::mouse_event([UiHelperWin32]::MOUSEEVENTF_LEFTUP,0,0,0,[UIntPtr]::Zero)
  Start-Sleep -Milliseconds $delayAfter
}

function CtrlClick-At($x, $y, $delayAfter = 400) {
  [UiHelperWin32]::keybd_event([UiHelperWin32]::VK_CONTROL, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 60
  [UiHelperWin32]::SetCursorPos($x, $y)
  Start-Sleep -Milliseconds 120
  [UiHelperWin32]::mouse_event([UiHelperWin32]::MOUSEEVENTF_LEFTDOWN,0,0,0,[UIntPtr]::Zero)
  Start-Sleep -Milliseconds 60
  [UiHelperWin32]::mouse_event([UiHelperWin32]::MOUSEEVENTF_LEFTUP,0,0,0,[UIntPtr]::Zero)
  Start-Sleep -Milliseconds 60
  [UiHelperWin32]::keybd_event([UiHelperWin32]::VK_CONTROL, 0, [UiHelperWin32]::KEYEVENTF_KEYUP, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds $delayAfter
}

function RightClick-At($x, $y, $delayAfter = 400) {
  [UiHelperWin32]::SetCursorPos($x, $y)
  Start-Sleep -Milliseconds 120
  [UiHelperWin32]::mouse_event([UiHelperWin32]::MOUSEEVENTF_RIGHTDOWN,0,0,0,[UIntPtr]::Zero)
  Start-Sleep -Milliseconds 60
  [UiHelperWin32]::mouse_event([UiHelperWin32]::MOUSEEVENTF_RIGHTUP,0,0,0,[UIntPtr]::Zero)
  Start-Sleep -Milliseconds $delayAfter
}

# Crops to the app's own window rect — prefer this over a full-desktop screenshot so
# other windows on the real desktop never end up in a captured image.
function Screenshot-AppWindow($procId, $outPath) {
  $rect = Get-AppRect $procId
  $width = $rect.Right - $rect.Left
  $height = $rect.Bottom - $rect.Top
  $bmp = New-Object System.Drawing.Bitmap $width, $height
  $gfx = [System.Drawing.Graphics]::FromImage($bmp)
  $gfx.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $width, $height))
  $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
  $gfx.Dispose(); $bmp.Dispose()
}

# Native Open/Save dialogs are separate top-level windows outside the app's rect, so they
# need a full-desktop capture to locate. Crop the result down immediately with Crop-Image
# and discard the raw full-desktop image — it will contain whatever else is on the real
# desktop (other apps, personal folder names, etc.), which must never end up in the repo.
function Screenshot-FullDesktop($outPath) {
  $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
  $gfx = [System.Drawing.Graphics]::FromImage($bmp)
  $gfx.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
  $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
  $gfx.Dispose(); $bmp.Dispose()
}

function Crop-Image($inPath, $outPath, $x, $y, $w, $h) {
  $src = [System.Drawing.Image]::FromFile($inPath)
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $gfx = [System.Drawing.Graphics]::FromImage($bmp)
  $gfx.DrawImage($src, (New-Object System.Drawing.Rectangle 0,0,$w,$h), (New-Object System.Drawing.Rectangle $x,$y,$w,$h), [System.Drawing.GraphicsUnit]::Pixel)
  $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
  $gfx.Dispose(); $bmp.Dispose(); $src.Dispose()
}

# Launches the app and blocks until its main window has a title/handle (or times out).
# Returns the process object.
function Start-AppAndWait($exePath, $timeoutSeconds = 20) {
  $workDir = Split-Path $exePath
  $proc = Start-Process -FilePath $exePath -WorkingDirectory $workDir -PassThru
  $deadline = (Get-Date).AddSeconds($timeoutSeconds)
  while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    $p = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
    if ($null -eq $p) { throw "Process exited before its window appeared" }
    if ($p.MainWindowHandle -ne 0 -and $p.MainWindowTitle -ne "") { return $p }
  }
  throw "Timed out waiting for main window"
}

# Types text into whatever control currently has focus (e.g. a native Open/Save dialog's
# filename box) and presses Enter. SendKeys treats { } + ^ % ~ ( ) specially — avoid paths
# containing those characters, or escape them, before calling this.
function Type-AndEnter($text, $delayAfter = 800) {
  [System.Windows.Forms.SendKeys]::SendWait($text)
  Start-Sleep -Milliseconds 300
  [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
  Start-Sleep -Milliseconds $delayAfter
}
