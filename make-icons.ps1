# Draws the plugin's PNG icons (1x and @2x) into the .sdPlugin\imgs folder.
param([string]$Out = "$PSScriptRoot\com.lee-cleobury.displayhotkeys.sdPlugin\imgs")

Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $Out, "$Out\keys" | Out-Null

function Save-Icon($name, $size, [scriptblock]$painter) {
    foreach ($scale in 1, 2) {
        $px = $size * $scale
        $bmp = New-Object System.Drawing.Bitmap $px, $px
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'
        $g.ScaleTransform($px / 100, $px / 100)   # draw on a 100x100 canvas
        & $painter $g
        $g.Dispose()
        $file = if ($scale -eq 1) { "$name.png" } else { "$name@2x.png" }
        $bmp.Save((Join-Path $Out $file), [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
    }
}

function New-Pen($color, $width) {
    $pen = New-Object System.Drawing.Pen $color, $width
    $pen.LineJoin = 'Round'
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $pen
}
function Brush($color) { New-Object System.Drawing.SolidBrush $color }
function Tint($color) { Brush ([System.Drawing.Color]::FromArgb(70, $color)) }

function Rounded($x, $y, $w, $h, $r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

# Screen with an outline, optional tint fill.
function Draw-Screen($g, $color, $filled, $x, $y, $w, $h, $stroke = 3.5) {
    $path = Rounded $x $y $w $h 2
    if ($filled) { $g.FillPath((Tint $color), $path) }
    $g.DrawPath((New-Pen $color $stroke), $path)
}

# Monitor glyph centred around (cx, cy) with the given width.
function Draw-Monitor($g, $color, $filled, $cx, $cy, $w) {
    $h = $w * 0.62
    $x = $cx - $w / 2; $y = $cy - $h / 2
    Draw-Screen $g $color $filled $x $y $w $h ($w * 0.07)
    $g.FillRectangle((Brush $color), $cx - $w * 0.06, $y + $h, $w * 0.12, $w * 0.12)
    $g.FillRectangle((Brush $color), $cx - $w * 0.22, $y + $h + $w * 0.11, $w * 0.44, $w * 0.06)
}

function Draw-Arrows($g, $color, $cx, $cy, $r) {
    $pen = New-Object System.Drawing.Pen $color, ($r * 0.22)
    $pen.EndCap = 'ArrowAnchor'
    $g.DrawArc($pen, $cx - $r, $cy - $r, 2 * $r, 2 * $r, 200, 120)
    $g.DrawArc($pen, $cx - $r, $cy - $r, 2 * $r, 2 * $r, 20, 120)
}

# Key glyphs, drawn in the top ~60% of the key (the profile name sits along the bottom).
$glyphs = [ordered]@{
    monitor   = { param($g, $c, $f) Draw-Monitor $g $c $f 50 36 52 }
    dual      = { param($g, $c, $f)
        Draw-Monitor $g $c $f 30 38 34
        Draw-Monitor $g $c $f 70 38 34
    }
    ultrawide = { param($g, $c, $f)
        $p = New-Object System.Drawing.Drawing2D.GraphicsPath
        $p.AddBezier(14, 18, 38, 23, 62, 23, 86, 18)
        $p.AddLine(86, 18, 86, 46)
        $p.AddBezier(86, 46, 62, 41, 38, 41, 14, 46)
        $p.CloseFigure()
        if ($f) { $g.FillPath((Tint $c), $p) }
        $g.DrawPath((New-Pen $c 3.5), $p)
        $g.FillRectangle((Brush $c), 47, 43, 6, 8)
        $g.FillRectangle((Brush $c), 38, 51, 24, 3.5)
    }
    tv        = { param($g, $c, $f)
        Draw-Screen $g $c $f 14 14 72 40 3
        $pen = New-Pen $c 3.5
        $g.DrawLine($pen, 24, 54, 20, 62)
        $g.DrawLine($pen, 76, 54, 80, 62)
    }
    pc        = { param($g, $c, $f)
        Draw-Monitor $g $c $f 38 36 46
        $tower = Rounded 68 14 18 46 3
        if ($f) { $g.FillPath((Tint $c), $tower) }
        $g.DrawPath((New-Pen $c 3.5), $tower)
        $g.FillEllipse((Brush $c), 74.5, 20, 5, 5)
        $g.DrawLine((New-Pen $c 2.5), 72, 30, 82, 30)
    }
    laptop    = { param($g, $c, $f)
        Draw-Screen $g $c $f 26 16 48 32
        $base = New-Object System.Drawing.Drawing2D.GraphicsPath
        $base.AddPolygon([System.Drawing.PointF[]]@(
            [System.Drawing.PointF]::new(20, 51), [System.Drawing.PointF]::new(80, 51),
            [System.Drawing.PointF]::new(86, 58), [System.Drawing.PointF]::new(14, 58)))
        $g.FillPath((Brush $c), $base)
    }
    projector = { param($g, $c, $f)
        $body = Rounded 18 28 64 26 6
        if ($f) { $g.FillPath((Tint $c), $body) }
        $g.DrawPath((New-Pen $c 3.5), $body)
        $g.DrawEllipse((New-Pen $c 3.5), 26, 33, 16, 16)
        $g.FillEllipse((Brush $c), 31, 38, 6, 6)
        $pen = New-Pen $c 3
        $g.DrawLine($pen, 54, 37, 72, 37)
        $g.DrawLine($pen, 54, 45, 72, 45)
        $g.DrawLine($pen, 26, 54, 24, 60)
        $g.DrawLine($pen, 74, 54, 76, 60)
        $beam = New-Pen ([System.Drawing.Color]::FromArgb(140, $c)) 2.5
        $g.DrawLine($beam, 30, 26, 22, 12)
        $g.DrawLine($beam, 34, 26, 34, 11)
        $g.DrawLine($beam, 38, 26, 46, 12)
    }
    controller = { param($g, $c, $f)
        $p = New-Object System.Drawing.Drawing2D.GraphicsPath
        $p.AddBezier(30, 20, 40, 22, 60, 22, 70, 20)
        $p.AddBezier(70, 20, 84, 18, 92, 44, 86, 54)
        $p.AddBezier(86, 54, 80, 62, 72, 56, 66, 46)
        $p.AddLine(66, 46, 34, 46)
        $p.AddBezier(34, 46, 28, 56, 20, 62, 14, 54)
        $p.AddBezier(14, 54, 8, 44, 16, 18, 30, 20)
        $p.CloseFigure()
        if ($f) { $g.FillPath((Tint $c), $p) }
        $g.DrawPath((New-Pen $c 3.5), $p)
        $b = Brush $c
        $g.FillRectangle($b, 24, 30.5, 14, 4)
        $g.FillRectangle($b, 29, 25.5, 4, 14)
        $g.FillEllipse($b, 64, 26, 5.5, 5.5)
        $g.FillEllipse($b, 70, 32, 5.5, 5.5)
    }
}

$white = [System.Drawing.Color]::White
$blue = [System.Drawing.Color]::FromArgb(106, 167, 255)
$green = [System.Drawing.Color]::FromArgb(111, 207, 124)
$bg = [System.Drawing.Color]::Black   # pure black so OLED keys stay off behind the glyph

foreach ($name in $glyphs.Keys) {
    $glyph = $glyphs[$name]
    Save-Icon "keys\$name" 72 { param($g) $g.Clear($bg); & $glyph $g $white $false }
    Save-Icon "keys\$name-active" 72 { param($g)
        $g.Clear($bg)
        & $glyph $g $green $true
        $g.FillRectangle((Brush $green), 0, 0, 100, 5)
    }
}
# Manifest default images = the plain monitor.
Copy-Item "$Out\keys\monitor.png" "$Out\key.png"; Copy-Item "$Out\keys\monitor@2x.png" "$Out\key@2x.png"
Copy-Item "$Out\keys\monitor-active.png" "$Out\key-active.png"; Copy-Item "$Out\keys\monitor-active@2x.png" "$Out\key-active@2x.png"

# Small monochrome icons for the action list / category.
Save-Icon 'category' 28 { param($g) Draw-Monitor $g $white $false 50 44 84 }
Save-Icon 'action-switch' 20 { param($g) Draw-Monitor $g $white $false 50 44 84 }
Save-Icon 'action-cycle' 20 { param($g) Draw-Arrows $g $white 50 50 36 }

# Plugin icon.
Save-Icon 'plugin' 256 { param($g)
    $g.Clear($bg)
    Draw-Monitor $g $blue $true 40 42 46
    Draw-Monitor $g $white $true 62 58 46
}

Save-Icon 'key-cycle' 72 { param($g)
    $g.Clear($bg)
    Draw-Monitor $g $white $false 50 36 40
    Draw-Arrows $g $blue 50 38 34
}
