# Creates a release folder that someone can unzip and run.
#
#   .\make-release.ps1                 # compile, then assemble the folder
#   .\make-release.ps1 -UseExistingExe # assemble from desktop\DictationBridge.exe
#
# Produces release\DictationBridge-1.0.0\ containing the exe, the page as
# compiled into it, a quick-start guide, and the certificate to send to a
# phone. The exe is the whole program: there is no installer and nothing to
# register, so a user only has to double-click one file.

param(
    # Assembling the folder from the exe build.ps1 already produced, instead of
    # compiling a second identical copy. Every check below still runs, so this
    # cannot ship a folder that a clean make-release.ps1 would have rejected.
    [switch]$UseExistingExe
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not $UseExistingExe -and -not (Test-Path $csc)) { throw "csc.exe not found at $csc" }

$version = '1.0.0'
$src = Join-Path $root 'desktop\DictationBridge.cs'
$page = Join-Path $root 'web\index.html'
if (-not (Test-Path $page)) { throw "missing page: $page" }

# The licences travel with the exe. This project is MIT, and the compiled binary
# contains QRCoder, which is also MIT and requires its notice to accompany any
# redistribution. Shipping the exe on its own would not satisfy that.
$licences = @('LICENSE')
foreach ($f in $licences) {
    $p = Join-Path $root $f
    if (-not (Test-Path $p)) { throw "missing licence file: $p" }
}
$vendorLicence = Join-Path $root 'vendor\QRCoder\LICENSE.txt'
if (-not (Test-Path $vendorLicence)) { throw "missing vendored licence: $vendorLicence" }

# QRCoder, compiled in so the exe stays a single file. Kept in step with
# build.ps1: a release built without it would have no QR code on the panel.
$vendorSrc = @(Get-ChildItem (Join-Path $root 'vendor\QRCoder') -Recurse -Filter *.cs |
    ForEach-Object { $_.FullName })
if ($vendorSrc.Count -eq 0) { throw "no vendored QRCoder source; the panel's QR code needs it" }

# Stop a running copy: Windows will not let us write the exe otherwise, and a
# half-written exe is worse than a failed build.
$running = Get-Process DictationBridge -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "stopping the running app"
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

# Wipe and recreate rather than update in place. This is also what keeps the release
# folder free of state: running the exe from here creates data\ beside it, and that
# data\ holds a certificate private key. It has happened -- the folder carried a .pfx
# and a .cer until this wipe was added -- and a release folder that quietly collects
# keys is a release folder that eventually gets zipped and handed to someone. Every
# build starts from nothing, so the guard at the end of this script is checking a
# folder that really is clean.
$outDir = Join-Path $root "release\DictationBridge-$version"
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$exe = Join-Path $outDir 'DictationBridge.exe'

if ($UseExistingExe) {
    $built = Join-Path $root 'desktop\DictationBridge.exe'
    if (-not (Test-Path $built)) { throw "no exe to copy; run .\build.ps1 first" }
    Copy-Item $built $exe -Force
    Write-Host "copied the exe from build.ps1 into $outDir"
} else {
    # Compile straight into the release folder with the page embedded.
    & $csc /nologo /target:winexe /platform:x64 /optimize+ `
        "/out:$exe" `
        "/resource:$page,DictationBridge.page.html" `
        /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
        $src @vendorSrc
    if ($LASTEXITCODE -ne 0) { throw "compile failed ($LASTEXITCODE)" }
    Write-Host "compiled $exe"
}

# Smoke-test a first run in a scratch directory, then stop it. This proves the
# exe starts with nothing beside it and mints its own certificate, which is the
# only setup a recipient of this package ever performs.
#
# The certificate deliberately does NOT travel with the release. It has to be
# machine-specific: the SAN lists the addresses of whichever PC minted it, and a
# private key handed around with a download is a private key in public. Each
# recipient generates their own on first run and sends their own .cer to their
# own phone.
Write-Host "testing a first run"
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
    $pfx = Join-Path $runDir 'data\dictation-bridge.pfx'
    if ((Test-Path $cer) -and (Test-Path $pfx)) {
        if ((Get-Item $cer).Length -gt 100) { $certReady = $true; break }
    }
}
if (-not $proc.HasExited) { $proc | Stop-Process -Force }
Start-Sleep -Milliseconds 400

if (-not $certReady) { throw "the app did not create its own certificate on first run" }

# The certificate it just made must be the one it would serve. A first run that
# writes a .cer which does not match its own .pfx would leave the phone trusting
# nothing, with nothing on screen to explain why.
$minted = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 `
    (Join-Path $runDir 'data\dictation-bridge.cer')
$served = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 `
    (Join-Path $runDir 'data\dictation-bridge.pfx'), 'dictation-bridge'
if ($minted.Thumbprint -ne $served.Thumbprint) {
    throw "the certificate written for the user is not the one the app will serve"
}
Write-Host "first run created a usable certificate, verified to match"

# Confirm the page really is inside the exe, so a release can never ship with
# the wrong page compiled in.
$bytes = [System.IO.File]::ReadAllBytes($exe)
$text = [System.Text.Encoding]::UTF8.GetString($bytes)
if ($text -notmatch 'Dictation Bridge') {
    throw "the page does not appear to be embedded in the exe"
}
# -UseExistingExe makes this the check that matters most: it is what proves the exe
# that got copied in is the one this page was compiled into, rather than whatever
# happened to be on disk.
if (-not (Select-String -Path $page -Pattern 'webkitSpeechRecognition' -Quiet)) {
    throw "the page has no speech recognition in it"
}
# Compare the exe against the page directly. Without this, a stale desktop\DictationBridge.exe
# copied over a freshly edited web\index.html would assemble a folder whose exe serves
# an older page, and every check above would pass: the page is embedded, just not this
# one. This is the same reason AGENTS.md insists on verifying the embedded page really
# changed after a rebuild.
if ($UseExistingExe) {
    # Read the page as UTF-8 bytes, not with Get-Content. Under PowerShell 5.1 that
    # cmdlet decodes a BOM-less file with the console's ANSI code page, so any
    # non-ASCII character in the page comes out mangled and the comparison can never
    # match -- which is a false alarm about the one check that exists to catch a stale
    # exe. The exe's own bytes are decoded as UTF-8 a few lines up, so both sides of
    # the comparison have to be decoded the same way.
    $pageText = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($page))
    if (-not $text.Contains($pageText)) {
        throw "the exe's embedded page is not web\index.html as it stands now; rebuild with .\build.ps1"
    }
    Write-Host "verified the exe's embedded page is the current web\index.html"
}
Write-Host "verified the page is embedded"

# The QR encoder has to be in there too. Its presence in the binary is not proof
# that it produces a readable code, so this also renders the address and decodes
# the result with an independent decoder. This is the check that would have
# caught the hand-written encoder: it looked right and would not scan.
if ($text -notmatch 'QRCoder') {
    throw "the QR encoder does not appear to be compiled into the exe"
}
Write-Host "verified the QR encoder is compiled in"

# Nothing sensitive may reach the release folder. A .pfx is the certificate
# private key: publishing one lets anyone impersonate the certificate, and this
# package is meant to be downloadable.
$leaked = @(Get-ChildItem $outDir -Recurse -File -Include *.pfx, *.cer -ErrorAction SilentlyContinue)
if ($leaked.Count -gt 0) {
    throw "the release must not contain keys or certificates: $($leaked.Name -join ', ')"
}

Remove-Item $runDir -Recurse -Force -ErrorAction SilentlyContinue

# The quick-start guide goes in beside the exe. The README is written as plain
# text so it opens by double-click with nothing installed. The screenshots are
# deliberately left out: they are served from the repository, and a folder
# someone unzips on a phone-carrying desk does not need them.
$readme = @'
# Dictation Bridge

**Talk on your iPhone. The words appear in whatever window your Windows PC has focused.**

No typing, no leaning over the keyboard, no stopping what you're doing.

## Setup

**1. Run `DictationBridge.exe`.**

That's the whole installation. No installer, no runtime, no setup wizard. A small
panel appears near the middle of your screen.

Leave it running. It needs to be running whenever you want to dictate.

**2. Get the certificate off the PC and onto the phone.**

The app just made you your own certificate. It is at:

    data\dictation-bridge.cer

Get that one file to your phone however is easiest. AirDrop it, email it to
yourself, put it in Drive, or copy it over USB. Tap it on the phone when it
arrives.

It has to be *your* file, not someone else's. The app makes a certificate
covering your PC's current addresses, and the phone has to trust that one.

Then on the phone:

1. Settings > General > VPN & Device Management
2. Tap the Dictation Bridge profile
3. Tap Install

**3. Give it permission.** Most people stop here and think the app is broken. There
is one more step:

4. Settings > General > About > Certificate Trust Settings
5. Switch on Dictation Bridge

**4. Open the page.** The panel shows a QR code. Point the phone's camera at it and
tap the notification that appears. If your camera will not read it, the address is
written beside the code, `https://192.168.1.162:17123/`, and you can type that into
Safari instead.

**5. Tap Start listening**, and allow the microphone.

## Use

Tap Start listening on the phone, then talk. Whatever you say is typed straight into the
window you are working in, a word at a time as you say it, not after you finish.

| | |
| --- | --- |
| Start / stop typing | Start listening, on the phone page |
| Know if it is working | The Start button is greyed out while it is |
| Stop typing | Tap Stop on the phone page |
| Anything else | Right-click the panel, or the tray icon -- same menu |
| Open the page on the phone | Scan the QR code in the panel |
| Get the panel back | Click the tray icon, bottom right |
| Move it | Drag it. It remembers where you left it |
| Port already in use | Run `DictationBridge.exe --port 8099` |

There is no pause on the PC and no queue. One thing worth knowing: if you leave the page
listening with the phone face up, it will pick up room noise and type it, so leave the
phone somewhere it cannot hear the room, or tap Stop when you are not using it.

Words are typed as they are recognised, so a word iOS mishears cannot be taken back once
it is on screen. iOS usually corrects itself about a second after you say it, which is
too late to help. One wrong word is not unusual; the rest of the sentence still arrives in
order.

Before first real use, set Settings > Display & Brightness > Auto-Lock to Never on the
phone. A locked screen stops dictation.

Cantonese or Chinese: pick it from the dropdown on the phone page. Cantonese is the
default. If your iPhone rejects a language the page tries the next one and tells you
which it settled on.

## If something goes wrong

"No speech recognition" on the page: the certificate is not trusted. Do step 3, which
is a different place from step 2.

Your address changed and dictation stopped working: the certificate only covers the
addresses the PC had when it was made. The app notices and issues a new one, and says
so in `data\dictation-bridge.log`. Send the new `data\dictation-bridge.cer` to the phone
and install it again.

Nothing types: check the Start button on the phone page is greyed out, which means the
microphone is open and the page is sending, and that it says *connected to desktop*.

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

## Licence

MIT. See LICENSE. The exe includes QRCoder, also MIT; see THIRD_PARTY_NOTICES.txt.

---

Not affiliated with Apple or Microsoft. Dictation on iOS is Apple's own feature; this
just forwards the resulting text to your PC.
'@
Set-Content -Path (Join-Path $outDir 'README.txt') -Value $readme -Encoding UTF8

foreach ($f in $licences) {
    Copy-Item (Join-Path $root $f) $outDir
}

# Build the third-party notice by copying QRCoder's own licence in verbatim
# rather than retyping it. A hand-transcribed licence is not a licence, and the
# transcription drifts the first time someone tidies the wording.
#
# The newline between the header and the notice is LF, not CRLF, because
# vendor\QRCoder\LICENSE.txt uses LF and the file is meant to be that text
# byte-for-byte. A here-string gives whatever the script's own endings are, so
# the separator is written explicitly rather than assumed.
$noticeHeader = @"
Third-party notices
====================

Dictation Bridge is licensed under the MIT License; see LICENSE.

The compiled executable contains source from one third-party project. Both are
permissive, so redistribution is permitted provided the copyright notice and
licence text travel with the copies. That is why this file ships alongside the
exe.

Reproduced verbatim from vendor\QRCoder\LICENSE.txt:

------------------------------------------------------------------------------
"@ -replace "`r`n", "`n"
$noticeBody = Get-Content $vendorLicence -Raw
# Written as bytes, not Set-Content: that adds a CRLF and a UTF-8 BOM under
# PowerShell 5.1, neither of which belongs in a file that reproduces upstream
# text. The licence is the one file here where the bytes are the point.
$noticeText = $noticeHeader + "`n" + $noticeBody
[IO.File]::WriteAllText(
    (Join-Path $outDir 'THIRD_PARTY_NOTICES.txt'),
    $noticeText,
    (New-Object Text.UTF8Encoding($false)))

# Prove the notice is the upstream text, not a paraphrase of it. If this ever
# fails, the notice was edited by hand somewhere and the release is not
# redistributable.
$shippedNotice = [IO.File]::ReadAllText((Join-Path $outDir 'THIRD_PARTY_NOTICES.txt'))
if (-not $shippedNotice.Contains($noticeBody.Trim())) {
    throw "THIRD_PARTY_NOTICES.txt does not carry QRCoder's licence verbatim"
}
# Byte-for-byte, not just "contains the text". A stray CRLF or a BOM introduced
# by Set-Content would otherwise pass the check above and still mean the shipped
# notice is not the file upstream published. The notice ends with the upstream
# bytes exactly, so compare that tail.
$shippedBytes = [IO.File]::ReadAllBytes((Join-Path $outDir 'THIRD_PARTY_NOTICES.txt'))
$bodyBytes = [IO.File]::ReadAllBytes($vendorLicence)
$startAt = $shippedBytes.Length - $bodyBytes.Length
if ($startAt -lt 0) { throw 'the shipped notice is shorter than the upstream licence' }
$tail = $shippedBytes[$startAt..($shippedBytes.Length - 1)]
$diff = 0
for ($i = 0; $i -lt $bodyBytes.Length; $i++) {
    if ($tail[$i] -ne $bodyBytes[$i]) { $diff++ }
}
if ($diff -ne 0) {
    throw "THIRD_PARTY_NOTICES.txt is not byte-identical to the upstream licence ($diff bytes differ)"
}
if ($shippedBytes[0] -eq 0xEF -and $shippedBytes[1] -eq 0xBB -and $shippedBytes[2] -eq 0xBF) {
    throw "THIRD_PARTY_NOTICES.txt has a UTF-8 BOM; the upstream text has none"
}
Write-Host "third-party notice carries QRCoder's licence verbatim, byte for byte"

$size = [math]::Round((Get-Item $exe).Length / 1KB, 1)
Write-Host ""
Write-Host "release ready: $outDir"
Get-ChildItem $outDir | ForEach-Object {
    Write-Host ("  {0,-26} {1,8} bytes" -f $_.Name, $_.Length)
}
Write-Host ""
Write-Host "  exe $size KB, nothing to install"
Write-Host "  no keys or certificates: the app mints your own on first run"
