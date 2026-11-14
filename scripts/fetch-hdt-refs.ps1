# 從官方 HDT 發行版（HearthSim/HDT-Velopack-Releases）下載最新完整套件，
# 取出編譯外掛需要的參考檔（HearthstoneDeckTracker.exe、HearthDb.dll）。
# 這些檔案只在編譯時使用，不會放進 repo，也不會打包進安裝程式。
param(
    [string]$OutDir = "hdt-ref"
)
$ErrorActionPreference = "Stop"

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("hdt-pkg-" + [guid]::NewGuid())
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

Write-Host "Downloading latest official HDT package..."
gh release download --repo HearthSim/HDT-Velopack-Releases --pattern "*-full.nupkg" --dir $tmp
if ($LASTEXITCODE -ne 0) { throw "gh release download failed" }

$pkg = Get-ChildItem $tmp -Filter "*-full.nupkg" | Select-Object -First 1
if (-not $pkg) { throw "No *-full.nupkg found in latest release" }
Write-Host "Package: $($pkg.Name)"

$zip = Join-Path $tmp "pkg.zip"
Copy-Item $pkg.FullName $zip
$extract = Join-Path $tmp "x"
Expand-Archive -Path $zip -DestinationPath $extract

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
foreach ($name in @("HearthstoneDeckTracker.exe", "HearthDb.dll")) {
    $f = Get-ChildItem $extract -Recurse -Filter $name | Select-Object -First 1
    if (-not $f) { throw "$name not found inside package" }
    Copy-Item $f.FullName (Join-Path $OutDir $name) -Force
    Write-Host "  $name <- $($f.FullName.Substring($extract.Length))"
}

# 記下編譯時對應的 HDT 版本
$pkg.Name | Out-File (Join-Path $OutDir "hdt-version.txt") -Encoding utf8
