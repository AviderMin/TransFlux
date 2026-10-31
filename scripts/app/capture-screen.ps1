# Captures the primary screen to a PNG for visual evidence.
param([string]$Out = 'docs/evidence/screenshots/desktop.png')
Add-Type -AssemblyName System.Windows.Forms,System.Drawing
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$path = Join-Path $root $Out
New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
$bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host ("Saved " + $path + " " + $b.Width + "x" + $b.Height)
