# Creates a release folder that someone can unzip and run.
#
#   .\make-release.ps1
#
# Produces release\DictationBridge-1.0.0\ containing the exe, the page as
# compiled into it, a quick-start guide, and the certificate to send to a
# phone. The exe is the whole program: there is no installer and nothing to
# register, so a user only has to double-click one file.

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }

$version = '1.0.0'
$src = Join-Path $root 'desktop\DictationBridge.cs'
$page = Join-Path $root 'web\index.html'
if (-not (Test-Path $page)) { throw "missing page: $page" }

# Stop a running copy: Windows will not let us write the exe otherwise, and a
# half-written exe is worse than a failed build.
$running = Get-Process DictationBridge -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "stopping the running app"
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

$outDir = Join-Path $root "release\DictationBridge-$version"
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$exe = Join-Path $outDir 'DictationBridge.exe'

# Compile straight into the release folder with the page embedded.
& $csc /nologo /target:winexe /platform:x64 /optimize+ `
    "/out:$exe" `
    "/resource:$page,DictationBridge.page.html" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    $src
if ($LASTEXITCODE -ne 0) { throw "compile failed ($LASTEXITCODE)" }

Write-Host "compiled $exe"

# Generate the certificate by running the exe briefly, then stop it. The cert
# is machine-specific, so it is produced per release machine rather than shipped
# in git.
Write-Host "generating the certificate"
$runDir = Join-Path $env:TEMP "db-release-$version"
if (Test-Path $runDir) { Remove-Item $runDir -Recurse -Force }
New-Item -ItemType Directory -Path $runDir -Force | Out-Null
Copy-Item $exe $runDir

$proc = Start-Process -FilePath (Join-Path $runDir 'DictationBridge.exe') `
    -WorkingDirectory $runDir -PassThru -WindowStyle Hidden
$certReady = $false
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 250
    $cer = Join-Path $runDir 'data\dictation-bridge.cer'
    if (Test-Path $cer) {
        $len = (Get-Item $cer).Length
        if ($len -gt 100) { $certReady = $true; break }
    }
}
if (-not $proc.HasExited) { $proc | Stop-Process -Force }
Start-Sleep -Milliseconds 400

if (-not $certReady) { throw "the app did not produce a certificate" }
# The app reuses the saved certificate, so shipping the data folder means the
# certificate we hand the user is the one it actually serves with. Shipping only
# the .cer would be useless: a first run with no data\ folder mints a different
# certificate and the phone would trust the wrong one.
New-Item -ItemType Directory -Path (Join-Path $outDir 'data') -Force | Out-Null
Copy-Item (Join-Path $runDir 'data\dictation-bridge.cer') $outDir
Copy-Item (Join-Path $runDir 'data\dictation-bridge.pfx') (Join-Path $outDir 'data')
# Also inside data\, because that is where the app looks on an upgrade and
# because a user's instructions will point at it.
Copy-Item (Join-Path $runDir 'data\dictation-bridge.cer') (Join-Path $outDir 'data')

# Prove the two are the same certificate. Shipping a .cer that does not match
# the served one means the phone trusts nothing and the app is unusable.
$shipped = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 `
    (Join-Path $outDir 'dictation-bridge.cer')
$served = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 `
    (Join-Path $outDir 'data\dictation-bridge.pfx'), 'dictation-bridge'
if ($shipped.Thumbprint -ne $served.Thumbprint) {
    throw "shipped certificate does not match the one the app will serve"
}
Write-Host "certificate ready and verified to match"

# Confirm the page really is inside the exe, so a release can never ship with
# the wrong page compiled in.
$bytes = [System.IO.File]::ReadAllBytes($exe)
$text = [System.Text.Encoding]::UTF8.GetString($bytes)
if ($text -notmatch 'Dictation Bridge') {
    throw "the page does not appear to be embedded in the exe"
}
if (-not (Select-String -Path $page -Pattern 'webkitSpeechRecognition' -Quiet)) {
    throw "the page has no speech recognition in it"
}
Write-Host "verified the page is embedded"

Remove-Item $runDir -Recurse -Force -ErrorAction SilentlyContinue

# The quick-start guide and the screenshots go in beside the exe. The README is
# written as plain text so it opens by double-click with nothing installed.
$readme = @'
# Dictation Bridge

**Talk on your iPhone. The words appear in whatever window your Windows PC has focused.**

No typing, no leaning over the keyboard, no stopping what you're doing.

## Setup

**1. Run `DictationBridge.exe`.**

That's the whole installation. No installer, no runtime, no setup wizard. A small
panel appears near the middle of your screen.

**2. On your phone, send it the certificate.**

The file `dictation-bridge.cer` is in this folder. AirDrop it, email it to yourself,
or put it in Drive, however is easiest. Tap it on the phone when it arrives.

Then on the phone:

1. Settings > General > VPN & Device Management
2. Tap the Dictation Bridge profile
3. Tap Install

**3. Give it permission.** Most people stop here and think the app is broken. There
is one more step:

4. Settings > General > About > Certificate Trust Settings
5. Switch on Dictation Bridge

**4. Open the page.** Your panel shows an address like `https://192.168.1.162:8080/`.
Type it into Safari on the phone.

**5. Tap Start listening**, and allow the microphone.

## Use

Press Ctrl+Alt+D on the PC. The panel says LISTENING. Now talk. Press it again to
pause. While paused your speech is queued, and typed the moment you press play, so
nothing is lost.

| | |
| --- | --- |
| Start / stop typing | Ctrl+Alt+D, or click the panel button |
| Get the panel back | Click the tray icon, bottom right |
| Move it | Drag it. It remembers where you left it |
| Use a different key | Expand the panel, press Change hotkey |
| Port already in use | Run `DictationBridge.exe --port 8099` |

Before first real use, set Settings > Display & Brightness > Auto-Lock to Never on
the phone. A locked screen stops dictation.

Cantonese or Chinese: pick it from the dropdown on the phone page. Cantonese is the
default. If your iPhone rejects a language the page tries the next one and tells you
which it settled on.

## If something goes wrong

"No speech recognition" on the page: the certificate is not trusted. Do step 3, which
is a different place from step 2.

Nothing types: the panel must say LISTENING.

Nothing appears in the target program: it may be running as administrator, which
Windows blocks. `data\dictation-bridge.log` will say `SendInput sent 0/44`.

Still stuck: on the phone page tap Send to desktop, then read
`data\diagnostics.log`. It records what the speech recogniser was doing.

## Requirements

- Windows 10 or later, 64-bit
- iPhone or iPad, iOS 14.5 or newer, Safari
- Both on the same network

## Privacy

Your speech is transcribed by Apple, because that is how Safari's dictation works.
The text goes to your own computer over your own network. Nothing is sent elsewhere.

Anyone on the same network who learns the session token can type into your focused
window. Fine on a home network, worth knowing on shared WiFi.

## Limits

The phone screen must stay on and unlocked. It cannot type into a program running as
administrator. If your PC's address changes to one the certificate does not list, you
need to send the certificate to the phone once more.

---

Not affiliated with Apple or Microsoft. Dictation on iOS is Apple's own feature; this
just forwards the resulting text to your PC.
'@
Set-Content -Path (Join-Path $outDir 'README.txt') -Value $readme -Encoding UTF8

foreach ($shot in @('panel-expanded.png', 'panel-collapsed.png', 'phone-page.png')) {
    $src = Join-Path $root "docs\$shot"
    if (Test-Path $src) { Copy-Item $src $outDir }
}

$size = [math]::Round((Get-Item $exe).Length / 1KB, 1)
Write-Host ""
Write-Host "release ready: $outDir"
Get-ChildItem $outDir | ForEach-Object {
    Write-Host ("  {0,-26} {1,8} bytes" -f $_.Name, $_.Length)
}
Write-Host ""
Write-Host "  exe $size KB, nothing to install"
