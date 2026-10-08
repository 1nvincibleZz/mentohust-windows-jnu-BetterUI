param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $PSScriptRoot 'Assets' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
[xml]$svg = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'Assets\AppEmblem.svg')
# The official SVG's first root group is the seal; the second is the wordmark.
$seal = $svg.DocumentElement.SelectSingleNode('*[local-name()="g"][1]')
$drawing = New-Object Windows.Media.DrawingGroup
foreach ($path in $seal.SelectNodes('.//*[local-name()="path"]')) {
    if ($path.HasAttribute('transform') -or $path.HasAttribute('style')) { throw 'Unsupported SVG path attributes.' }
    $geometry = [Windows.Media.Geometry]::Parse('F1 ' + $path.GetAttribute('d'))
    $brush = [Windows.Media.BrushConverter]::new().ConvertFromInvariantString($path.GetAttribute('fill'))
    $drawing.Children.Add([Windows.Media.GeometryDrawing]::new($brush, $null, $geometry))
}
if ($drawing.Children.Count -eq 0) { throw 'The official SVG contains no seal paths.' }
$drawing.Freeze()
$bounds = $drawing.Bounds
$extent = [Math]::Max($bounds.Width, $bounds.Height)
$frames = @()
foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
    $visual = New-Object Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    # Half a pixel protects the outer rim; the rest of the square stays transparent.
    $scale = ($size - 1.0) / $extent
    $context.PushTransform([Windows.Media.MatrixTransform]::new($scale, 0, 0, $scale,
        ($size - $bounds.Width * $scale) / 2 - $bounds.X * $scale,
        ($size - $bounds.Height * $scale) / 2 - $bounds.Y * $scale))
    # Retain the official white interior without a square background.
    $context.DrawEllipse([Windows.Media.Brushes]::White, $null,
        [Windows.Point]::new($bounds.X + $bounds.Width / 2, $bounds.Y + $bounds.Height / 2),
        $bounds.Width / 2, $bounds.Height / 2)
    $context.DrawDrawing($drawing)
    $context.Pop()
    $context.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object IO.MemoryStream
    try {
        $encoder.Save($stream)
        $frames += [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
    } finally { $stream.Dispose() }
}
$file = [IO.File]::Create((Join-Path $output 'AppIcon.ico'))
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose() }
[IO.File]::WriteAllBytes((Join-Path $output 'AppIcon-preview.png'), $frames[-1].Bytes)
Write-Output "APP_ICON_BUILD_PASS: $output/AppIcon.ico ($($frames.Count) transparent sizes)"
