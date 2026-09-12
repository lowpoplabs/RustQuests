# Renders assets/pamphlets/src/*.html -> assets/pamphlets/*.jpg at 1708x960 (2x the
# Rust photo-item base res — text stays sharp in the inspect view; FakeFriends v0.15.1 finding).
# Requires Chrome. JPGs must stay <= 512KB (RustQuests LoadPamphletCache limit).
$ErrorActionPreference = "Stop"
$chrome = "C:\Program Files\Google\Chrome\Application\chrome.exe"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir = Join-Path $root "src"
$tmp = Join-Path $env:TEMP "rq-pamphlet-render"
New-Item -ItemType Directory -Force $tmp | Out-Null

Add-Type -AssemblyName System.Drawing

foreach ($html in Get-ChildItem $srcDir -Filter *.html) {
    $png = Join-Path $tmp ($html.BaseName + ".png")
    $jpg = Join-Path $root ($html.BaseName + ".jpg")
    & $chrome --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=2 `
        --window-size=854,480 --screenshot="$png" "file:///$($html.FullName -replace '\\','/')" | Out-Null
    if (-not (Test-Path $png)) { throw "render failed: $($html.Name)" }

    $img = [System.Drawing.Image]::FromFile($png)
    try {
        $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq "image/jpeg" }
        $ep = New-Object System.Drawing.Imaging.EncoderParameters(1)
        $ep.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter([System.Drawing.Imaging.Encoder]::Quality, [long]82)
        if (Test-Path $jpg) { Remove-Item $jpg -Force }
        $img.Save($jpg, $codec, $ep)
    } finally { $img.Dispose() }

    $kb = [math]::Round((Get-Item $jpg).Length / 1KB, 1)
    if ((Get-Item $jpg).Length -gt 524288) { Write-Warning "$($html.BaseName).jpg is ${kb}KB - over the 512KB cache limit!" }
    Write-Output ("{0,-32} {1,7} KB" -f ($html.BaseName + ".jpg"), $kb)
}
