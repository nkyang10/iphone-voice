# Builds DictationBridge.exe with the web page embedded as a resource, and puts a copy
# in release\ where it can be copied to another machine and run.
#
#   .\build.ps1
#
# The release folder is always refreshed. There used to be a -NoRelease switch, and it
# was the wrong idea: csc does not produce byte-identical output for identical source,
# so skipping it leaves a genuinely older binary sitting next to the new one with
# nothing on screen saying which is which. That is the stale-exe trap this arrangement
# exists to remove, reintroduced through a convenience flag.
#
# The page is embedded so the exe is a single self-contained file. The desktop
# prefers the embedded copy, which means a file dropped next to the exe cannot
# change what the phone is served. A copy of web\index.html is still written
# alongside it so it stays editable and reviewable.
#
# Uses the .NET Framework csc.exe, which ships with Windows, so no SDK install
# and no network access are needed.

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    throw "csc.exe not found at $csc"
}

# Writing the exe fails while a copy is running, so stop it first. Report it
# loudly rather than letting csc print a confusing CS0016 and carry on with the
# previous binary.
$running = Get-Process DictationBridge -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "stopping the running app so the exe can be replaced"
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

$src = Join-Path $root 'desktop\DictationBridge.cs'
$page = Join-Path $root 'web\index.html'
$exe = Join-Path $root 'desktop\DictationBridge.exe'
$stage = Join-Path $root 'desktop\index.html'

foreach ($f in @($src, $page)) {
    if (-not (Test-Path $f)) { throw "missing input: $f" }
}

# QRCoder, vendored source. Compiled into the exe so the app still ships as one
# file; nothing here is loaded at runtime. See vendor\QRCoder\LICENSE.txt.
$vendor = Join-Path $root 'vendor\QRCoder'
$vendorSrc = @(Get-ChildItem $vendor -Recurse -Filter *.cs | ForEach-Object { $_.FullName })
if ($vendorSrc.Count -eq 0) { throw "no vendored QRCoder source under $vendor" }

# Keep the served copy in step with the source of truth.
Copy-Item $page $stage -Force

# Explicit resource name: the default would be namespace-prefixed and brittle.
& $csc /nologo /target:winexe /platform:x64 /optimize+ `
    "/out:$exe" `
    "/resource:$page,DictationBridge.page.html" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    $src @vendorSrc

if ($LASTEXITCODE -ne 0) { throw "compile failed ($LASTEXITCODE)" }

$size = [math]::Round((Get-Item $exe).Length / 1KB, 1)
Write-Host "built $exe  ($size KB)"
Write-Host "embedded: DictationBridge.page.html  ($([math]::Round((Get-Item $page).Length / 1KB, 1)) KB)"
Write-Host "vendored: QRCoder encoder  ($($vendorSrc.Count) files, compiled in)"

# Refresh release\DictationBridge-1.0.0 from what was just built.
#
# The exe that gets tested is the one that gets released. Having two copies is how a
# bug gets fixed in one and verified against the other -- the release folder held a
# 137 KB exe while the working build was 156 KB, which is exactly a stale-binary
# report waiting to happen. One build, one artefact, two locations.
#
# make-release.ps1 owns the folder's contents, its README and its licence checks, and
# it wipes the folder first so state from a test run -- including the certificate
# private key the app writes beside the exe -- can never survive into a package.
& (Join-Path $root 'make-release.ps1') -UseExistingExe
