# Package the portable, self-contained release assets for Windows x64.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\package-release.ps1
#   powershell ... -SkipPublish     # reuse an existing publish, only assemble + zip
#
# Output (build\dist\):
#   SandboxLauncher-<version>-win-x64.zip        GUI, self-contained  <- 主要下载
#   SandboxLauncher-cli-<version>-win-x64.zip    CLI, self-contained  <- 可选
#   *.sha256
#
# Both zips contain exactly one top-level folder, so unzipping never splatters.
# GUI zip layout:
#   SandboxLauncher-<version>-win-x64\SandboxLauncher.exe  + runtime dlls + README.md + LICENSE
# CLI zip layout:
#   SandboxLauncher-cli-<version>-win-x64\sl.exe           + runtime dlls + README.md + LICENSE
#
# NOTE: keep this file ASCII-only. Windows PowerShell 5.1 reads .ps1 files as ANSI
# unless they carry a UTF-8 BOM, and non-ASCII text then breaks parsing.

param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

# Self-contained publish needs the runtime packs. On machines whose NuGet.config only
# lists offline feeds this fails with NU1101, so restore explicitly against nuget.org.
$NugetSource = "https://api.nuget.org/v3/index.json"

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# --- version comes from the App csproj, single source of truth for the zip names ---
$csproj = Join-Path $repo "src\SandboxLauncher.App\SandboxLauncher.App.csproj"
$m = Select-String -Path $csproj -Pattern '<Version>([^<]+)</Version>'
if (-not $m) { throw "Cannot find <Version> in $csproj" }
$version = $m.Matches[0].Groups[1].Value.Trim()
Write-Step "version $version ($Configuration / $Runtime)"

$stageBase = Join-Path $repo "build\stage"
$zipDir    = Join-Path $repo "build\dist"
$guiPub    = Join-Path $repo "build\release\SandboxLauncher"
$cliPub    = Join-Path $repo "build\release\cli"

$guiStage = Join-Path $stageBase "SandboxLauncher-$version-$Runtime"
$cliStage = Join-Path $stageBase "SandboxLauncher-cli-$version-$Runtime"
$guiZip   = Join-Path $zipDir "SandboxLauncher-$version-$Runtime.zip"
$cliZip   = Join-Path $zipDir "SandboxLauncher-cli-$version-$Runtime.zip"

# --- a running exe locks its own dlls, so stop stragglers first ---
Write-Step "stopping running SandboxLauncher / sl processes"
Get-Process SandboxLauncher, sl -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($repo) } |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400

foreach ($p in @($guiStage, $cliStage)) {
    if (Test-Path $p) { Remove-Item $p -Recurse -Force }
    New-Item -ItemType Directory -Path $p -Force | Out-Null
}
New-Item -ItemType Directory -Path $zipDir -Force | Out-Null
foreach ($p in @($guiZip, $cliZip)) { if (Test-Path $p) { Remove-Item $p -Force } }

function Publish-Project($project, $outDir) {
    dotnet restore $project -r $Runtime -p:SelfContained=true --source $NugetSource
    if ($LASTEXITCODE -ne 0) { throw "restore failed for $project" }
    dotnet publish $project -c $Configuration -r $Runtime --self-contained true --no-restore -o $outDir
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $project" }
}

if ($SkipPublish) {
    Write-Step "reusing existing publish output"
    foreach ($d in @($guiPub, $cliPub)) {
        if (-not (Test-Path $d)) { throw "-SkipPublish was given but $d does not exist" }
    }
} else {
    Write-Step "publishing GUI (self-contained)"
    Publish-Project "src\SandboxLauncher.App" $guiPub
    Write-Step "publishing CLI (self-contained)"
    Publish-Project "src\SandboxLauncher.Cli" $cliPub
}

# --- assemble ---
Write-Step "assembling staging trees"
Copy-Item (Join-Path $guiPub "*") $guiStage -Recurse -Force
Copy-Item (Join-Path $cliPub "*") $cliStage -Recurse -Force

Write-Step "removing .pdb files"
foreach ($s in @($guiStage, $cliStage)) {
    Get-ChildItem $s -Recurse -Include *.pdb | Remove-Item -Force -ErrorAction SilentlyContinue
}

foreach ($s in @($guiStage, $cliStage)) {
    Copy-Item (Join-Path $repo "README.md") $s -Force
    if (Test-Path (Join-Path $repo "LICENSE")) {
        Copy-Item (Join-Path $repo "LICENSE") $s -Force
    } else {
        Write-Warning "LICENSE not found, package will ship without it"
    }
}

# --- sanity: both packages must carry their own runtime ---
Write-Step "verifying self-contained runtime payload"
foreach ($probe in @("hostfxr.dll", "coreclr.dll")) {
    if (-not (Test-Path (Join-Path $guiStage $probe))) { throw "GUI publish looks wrong: $probe missing" }
    if (-not (Test-Path (Join-Path $cliStage $probe))) { throw "CLI publish looks wrong: $probe missing" }
}
if (-not (Test-Path (Join-Path $guiStage "SandboxLauncher.exe"))) { throw "SandboxLauncher.exe missing" }
if (-not (Test-Path (Join-Path $cliStage "sl.exe"))) { throw "sl.exe missing" }

# --- zip both ---
function New-ReleaseZip($stage, $zipPath, $label) {
    Write-Step "compressing $label"
    Compress-Archive -Path $stage -DestinationPath $zipPath -CompressionLevel Optimal
    $zipMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
    $unpackedMb = [math]::Round((Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
    $fileCount = (Get-ChildItem $stage -Recurse -File).Count
    $hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
    "$hash  $(Split-Path -Leaf $zipPath)" | Set-Content -Path "$zipPath.sha256" -Encoding ASCII
    Write-Step ("  {0}: {1} MB compressed / {2} MB unpacked, {3} files, sha256 {4}" -f (Split-Path -Leaf $zipPath), $zipMb, $unpackedMb, $fileCount, $hash)
}

New-ReleaseZip $guiStage $guiZip "GUI package"
New-ReleaseZip $cliStage $cliZip "CLI package"

Write-Host ""
Write-Host "done." -ForegroundColor Green
