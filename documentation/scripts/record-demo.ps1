# Records the demo tour and cuts it into documentation GIFs and a promo video.
# Builds the app, runs it in --demo-tour mode (a visible, always-on-top window walking the
# features at a human pace, once per theme), records the window region with ffmpeg (gdigrab of
# the composited desktop - a plain window grab renders WebView2 content black), then cuts the
# chapters listed in chapters.json into GIFs (light pass) and an MP4 feature tour (dark pass).
#
# Requires ffmpeg on PATH (winget install Gyan.FFmpeg). The window sits top-left for ~4 minutes;
# it stays above other windows, but don't drag anything on top of it.
$ErrorActionPreference = "Stop"

$repo = Resolve-Path "$PSScriptRoot\..\.."
$app = "$repo\src\InventorMeta.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\InventorMeta.App.exe"
$samples = "$repo\src\InventorMeta.App\Assets\SampleFiles"
$model = "$samples\SampleBg\_Fishing Reel Assembly.iam"
$out = "$repo\documentation\.demo"
$gifDir = "$repo\documentation\public\images\app"

dotnet build "$repo\src\InventorMeta.App" -c Release -r win-x64 | Out-Null

$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg) {
    $ffmpeg = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\Gyan.FFmpeg*" -Recurse -Filter ffmpeg.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $ffmpeg) { throw "ffmpeg not found - winget install Gyan.FFmpeg" }

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out | Out-Null

# 1. the tour app writes window-rect.json, then waits for record-ready.flag
$appProc = Start-Process $app -ArgumentList "--demo-tour","`"$out`"","--samples","`"$samples`"","--model","`"$model`"" -PassThru
$rectFile = "$out\window-rect.json"
for ($i = 0; $i -lt 60 -and -not (Test-Path $rectFile); $i++) { Start-Sleep -Milliseconds 500 }
if (-not (Test-Path $rectFile)) { throw "window-rect.json never appeared" }
$rect = Get-Content $rectFile | ConvertFrom-Json
$w = [int]($rect.w - ($rect.w % 2)); $h = [int]($rect.h - ($rect.h % 2))

# 2. record that desktop region
$rec = "$out\tour.mkv"
$ffArgs = @("-y","-f","gdigrab","-framerate","30","-offset_x","$($rect.x)","-offset_y","$($rect.y)",
            "-video_size","${w}x${h}","-i","desktop","-c:v","libx264","-preset","veryfast","-crf","18",
            "-pix_fmt","yuv420p","$rec")
$ffProc = Start-Process $ffmpeg -ArgumentList $ffArgs -PassThru -WindowStyle Hidden -RedirectStandardError "$out\ffmpeg.log"
Start-Sleep -Seconds 3
Set-Content "$out\record-ready.flag" "go"

# 3. wait for the tour, then stop the recorder
for ($i = 0; $i -lt 600 -and -not (Test-Path "$out\tour-done.flag"); $i++) { Start-Sleep -Milliseconds 500 }
Start-Sleep -Seconds 2
if (-not $ffProc.HasExited) { Stop-Process -Id $ffProc.Id -Force -Confirm:$false }
if (-not $appProc.HasExited) { Stop-Process -Id $appProc.Id -Force -Confirm:$false -ErrorAction SilentlyContinue }

# 4. cut chapters: +2.5 s maps tour-clock time to video time (recorder spin-up before the flag)
$ch = (Get-Content "$out\chapters.json" | ConvertFrom-Json).chapters
$off = 2.5
function Chapter($name, $theme) { $ch | Where-Object { $_.name -eq $name -and $_.theme -eq $theme } }
function Gif($start, $dur, $name) {
    & $ffmpeg -y -v error -ss ($start + $off) -t $dur -i $rec -vf "fps=11,scale=820:-1:flags=lanczos,palettegen=stats_mode=diff" "$out\pal.png"
    & $ffmpeg -y -v error -ss ($start + $off) -t $dur -i $rec -i "$out\pal.png" -lavfi "fps=11,scale=820:-1:flags=lanczos[x];[x][1:v]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle" "$gifDir\$name"
    "{0}: {1:N2} MB" -f $name, ((Get-Item "$gifDir\$name").Length / 1MB)
}
$refs = Chapter "references" "light"; $orb = Chapter "viewer-orbit" "light"
$col = Chapter "coloring" "light"; $red = Chapter "redlining" "light"
$tabs = Chapter "tabs" "light"
Gif ($refs.startMs/1000 + 3.2) (($refs.endMs - $refs.startMs)/1000 - 3.6) "demo__references.gif"
Gif ($tabs.startMs/1000 + 0.2) (($tabs.endMs - $tabs.startMs)/1000 - 0.4) "demo__tabs.gif"
Gif ($orb.startMs/1000) (($col.endMs - $orb.startMs)/1000 - 0.3) "demo__viewer3d.gif"
Gif ($red.startMs/1000 + 0.5) (($red.endMs - $red.startMs)/1000 - 1.0) "demo__redlining.gif"

# 5. the full (light) pass becomes the promo / Store video
$h = Chapter "home" "light"; $o = Chapter "outro" "light"
& $ffmpeg -y -v error -ss ($h.startMs/1000 + $off) -t (($o.endMs - $h.startMs)/1000) -i $rec `
    -c:v libx264 -preset slow -crf 21 -pix_fmt yuv420p -movflags +faststart "$out\metareader-feature-tour.mp4"
"video: $out\metareader-feature-tour.mp4"
