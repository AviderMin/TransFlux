param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version,
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = 'x64',
    [string]$IsccPath,
    [switch]$PublishOnly
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (!$Version) {
    [xml]$properties = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw
    $Version = @($properties.Project.PropertyGroup.Version | Where-Object { $_ })[0]
}
$null = [version]::Parse($Version)
if (!$PublishOnly) {
    if (!$IsccPath) {
        $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($compiler) { $IsccPath = $compiler.Source }
        else {
            foreach ($programRoot in @([Environment]::GetFolderPath('ProgramFilesX86'), $env:ProgramFiles, (Join-Path $env:LOCALAPPDATA 'Programs'))) {
                if (!$programRoot) { continue }
                foreach ($major in @(7, 6)) {
                    $candidate = Join-Path $programRoot "Inno Setup $major/ISCC.exe"
                    if (Test-Path -LiteralPath $candidate) { $IsccPath = $candidate; break }
                }
                if ($IsccPath) { break }
            }
        }
    }
    if (!$IsccPath -or !(Test-Path -LiteralPath $IsccPath)) {
        throw 'Inno Setup compiler not found. Install Inno Setup 6.3+, pass -IsccPath, or use -PublishOnly.'
    }
}
$outputRoot = Join-Path $repoRoot "artifacts/installer/$Architecture"
$payload = Join-Path ([IO.Path]::GetTempPath()) ('TransFlux-payload-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $outputRoot -Force
$null = New-Item -ItemType Directory -Path $payload -Force
$proj = Join-Path $repoRoot 'src/TransFlux.App/TransFlux.App.csproj'
& dotnet clean $proj -c Release -r "win-$Architecture" "-p:Platform=$Architecture" -m:1 -v:quiet
if ($LASTEXITCODE -ne 0) { Write-Warning 'Release clean reported an error; continuing with publish.' }
& dotnet publish $proj -c Release -r "win-$Architecture" --self-contained true "-p:Platform=$Architecture" `
    -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true `
    -p:PublishTrimmed=false -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false `
    "-p:Version=$Version" -o $payload -m:1 -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
# Some WinUI native framework files reach TargetDir but are omitted from PublishDir; copy them in.
$targetDirRaw = & dotnet msbuild $proj -nologo -getProperty:TargetDir -p:Configuration=Release "-p:Platform=$Architecture" "-p:RuntimeIdentifier=win-$Architecture"
if ($LASTEXITCODE -ne 0) { throw 'Cannot locate TargetDir.' }
$targetDir = ($targetDirRaw | Out-String).Trim()
Get-ChildItem -LiteralPath $targetDir | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $payload -Recurse -Force }
# App assets (icon) for SetupIconFile.
Copy-Item -LiteralPath (Join-Path $repoRoot 'src/TransFlux.App/Assets') -Destination (Join-Path $payload 'Assets') -Recurse -Force
# Media core (shared FFmpeg build) + installed-layout manifest.
$ffBin = Join-Path $repoRoot 'tools/ffmpeg/bin'
$payloadFf = Join-Path $payload 'ffmpeg'
$null = New-Item -ItemType Directory -Path $payloadFf -Force
$mf = Get-Content (Join-Path $repoRoot 'tools/ffmpeg/core-manifest.json') -Raw | ConvertFrom-Json
foreach ($entry in @($mf.ffmpeg, $mf.ffprobe) + @($mf.runtimeFiles)) {
    $source = Join-Path (Join-Path $repoRoot 'tools/ffmpeg') $entry.relativePath
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Core hash mismatch: $source" }
    Copy-Item -LiteralPath $source -Destination $payloadFf -Force
}
$mf.ffmpeg.relativePath = 'ffmpeg.exe'
$mf.ffprobe.relativePath = 'ffprobe.exe'
foreach ($entry in $mf.runtimeFiles) { $entry.relativePath = Split-Path $entry.relativePath -Leaf }
$mf.reproducibilityNote = 'Installed layout: ffmpeg.exe/ffprobe.exe and av*.dll sit beside this manifest under <install>\ffmpeg.'
$mf | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $payloadFf 'core-manifest.json') -Encoding utf8
# Licenses / notices / provenance. Missing required license material must fail the build,
# not silently produce an installer with an incomplete notice set.
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $payload 'LICENSE.txt') -Force
$notices = Join-Path $payload 'THIRD-PARTY-NOTICES'
$null = New-Item -ItemType Directory -Path $notices -Force
$ffLicense = Join-Path $repoRoot 'tools/ffmpeg/LICENSE.txt'
if (!(Test-Path -LiteralPath $ffLicense)) { throw 'Bundled FFmpeg license is missing; refusing to package.' }
Copy-Item -LiteralPath $ffLicense -Destination (Join-Path $notices 'FFmpeg-LICENSE.txt') -Force
$revision = (& git -C $repoRoot rev-parse HEAD).Trim()
$statusText = ((& git -C $repoRoot status --porcelain) -join "`n")
$dirtyState = if ([string]::IsNullOrWhiteSpace($statusText)) { 'clean' } else {
    'dirty: ' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($statusText)))
}
$coreManifestHash = (Get-FileHash -LiteralPath (Join-Path $repoRoot 'tools/ffmpeg/core-manifest.json') -Algorithm SHA256).Hash
$dotnetVersion = (& dotnet --version).Trim()
@"
TransFlux source: local repository
Build base revision: $revision
Worktree state: $dirtyState
Build time (UTC): $((Get-Date).ToUniversalTime().ToString('o'))
Core manifest SHA-256: $coreManifestHash
Bundled media core: $($mf.distribution) $($mf.ffmpeg.version); see THIRD-PARTY-NOTICES/FFmpeg-LICENSE.txt and ffmpeg/core-manifest.json.
Toolchain: dotnet $dotnetVersion; Inno Setup $IsccPath
Application license: MIT; see LICENSE.txt.
"@ | Set-Content -LiteralPath (Join-Path $payload 'SOURCE.txt') -Encoding utf8
foreach ($required in @('TransFlux.exe', 'TransFlux.dll', 'TransFlux.pri', 'Reactor.pri', 'coreclr.dll', 'Assets/AppIcon.ico', 'ffmpeg/ffmpeg.exe', 'ffmpeg/core-manifest.json')) {
    if (!(Test-Path -LiteralPath (Join-Path $payload $required))) { throw "Publish output is missing $required" }
}
$payloadMb = [math]::Round((Get-ChildItem -LiteralPath $payload -Recurse -File | Measure-Object Length -Sum).Sum/1MB, 1)
Write-Output "Published payload: $payload ($payloadMb MB)"
if ($PublishOnly) { return }
& $IsccPath "/DAppVersion=$Version" "/DAppArchitecture=$Architecture" "/DPayloadDir=$payload" "/DOutputDir=$outputRoot" (Join-Path $repoRoot 'packaging/TransFlux.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
$installer = Join-Path $outputRoot "TransFlux-Setup-$Version-$Architecture.exe"
if (!(Test-Path -LiteralPath $installer)) { throw 'Installer output is missing.' }
(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash | Set-Content -LiteralPath "$installer.sha256" -Encoding ascii
Write-Output "Installer: $installer"
