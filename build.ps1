# Builds DictationBridge.exe with the web page embedded as a resource.
#
#   .\build.ps1
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

# Keep the served copy in step with the source of truth.
Copy-Item $page $stage -Force

# Explicit resource name: the default would be namespace-prefixed and brittle.
& $csc /nologo /target:winexe /platform:x64 /optimize+ `
    "/out:$exe" `
    "/resource:$page,DictationBridge.page.html" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    $src

if ($LASTEXITCODE -ne 0) { throw "compile failed ($LASTEXITCODE)" }

$size = [math]::Round((Get-Item $exe).Length / 1KB, 1)
Write-Host "built $exe  ($size KB)"
Write-Host "embedded: DictationBridge.page.html  ($([math]::Round((Get-Item $page).Length / 1KB, 1)) KB)"
