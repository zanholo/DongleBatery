# Gera src/MonitorBateria/Assets/app.ico (bateria com raio, vários tamanhos)
param([string]$Out = "$PSScriptRoot\..\src\MonitorBateria\Assets\app.ico")
Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($sz in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap $sz, $sz
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
  $s = $sz / 32.0
  # fundo arredondado
  $r = New-Object System.Drawing.RectangleF (1*$s), (1*$s), (30*$s), (30*$s)
  $d = 14*$s
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $p.AddArc($r.X, $r.Y, $d, $d, 180, 90); $p.AddArc($r.Right-$d, $r.Y, $d, $d, 270, 90)
  $p.AddArc($r.Right-$d, $r.Bottom-$d, $d, $d, 0, 90); $p.AddArc($r.X, $r.Bottom-$d, $d, $d, 90, 90); $p.CloseFigure()
  $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $r, ([System.Drawing.Color]::FromArgb(255, 0, 120, 212)), ([System.Drawing.Color]::FromArgb(255, 0, 64, 140)), 90
  $g.FillPath($bg, $p)
  # corpo da bateria
  $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.2, 2.2*$s))
  $g.DrawRectangle($pen, 6*$s, 10*$s, 18*$s, 12*$s)
  $g.FillRectangle([System.Drawing.Brushes]::White, 24.6*$s, 13.5*$s, 2.4*$s, 5*$s)
  $fill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 108, 203, 95))
  $g.FillRectangle($fill, 8.6*$s, 12.6*$s, 9.5*$s, 6.8*$s)
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $pngs += ,@($sz, $ms.ToArray())
}
$fs = [System.IO.File]::Create((Resolve-Path -LiteralPath (Split-Path $Out)).Path + "\" + (Split-Path $Out -Leaf))
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($e in $pngs) {
  $sz = $e[0]; $data = $e[1]
  $b = if ($sz -ge 256) { 0 } else { $sz }
  $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
  $offset += $data.Length
}
foreach ($e in $pngs) { $bw.Write($e[1]) }
$bw.Close()
"ok: $Out"
