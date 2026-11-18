# Creates a release folder that someone can unzip and run.
#
#   .\make-release.ps1                 # compile, then assemble the folder
#   .\make-release.ps1 -UseExistingExe # assemble from desktop\DictationBridge.exe
#
# Produces release\DictationBridge-<version>\ containing four files: the exe,
# README.txt, LICENSE and THIRD_PARTY_NOTICES.txt. The page travels compiled
# into the exe, not as a file beside it. No certificate ships in the package:
# each recipient's own certificate is minted beside the exe on their first
# run, which is what the wipe below and the leak guard at the end of this
# script are there to protect. The exe is the whole program: there is no
# installer and nothing to register, so a user only has to extract the folder
# and double-click one file.

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

# try/finally, not straight-line code: a throw anywhere in the test used to
# leave the scratch directory behind in %TEMP%, and it holds a freshly minted
# .pfx -- the private key this smoke test exists to prove is created. The
# finally stops the process too, so a failed check cannot leave the app
# running either.
$proc = $null
$certReady = $false
try {
    $proc = Start-Process -FilePath (Join-Path $runDir 'DictationBridge.exe') `
        -WorkingDirectory $runDir -PassThru -WindowStyle Hidden
    # All three, not just the certificate: the app writes pairing-key.txt a
    # moment after the cert files land, so a loop that stops at the first two
    # races the third and fails a working app at random.
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        $cer = Join-Path $runDir 'data\dictation-bridge.cer'
        $pfx = Join-Path $runDir 'data\dictation-bridge.pfx'
        $key = Join-Path $runDir 'data\pairing-key.txt'
        if ((Test-Path $cer) -and (Test-Path $pfx) -and (Test-Path $key)) {
            if ((Get-Item $cer).Length -gt 100) { $certReady = $true; break }
        }
    }

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

    # First run is not a success until the pairing key exists too. The QR code
    # carries it, so a build that mints a certificate but writes no
    # pairing-key.txt would pair nothing and could not be used at all.
    if (-not (Test-Path (Join-Path $runDir 'data\pairing-key.txt'))) {
        throw "the app did not create its pairing key on first run"
    }
} finally {
    if ($proc -and -not $proc.HasExited) { $proc | Stop-Process -Force }
    Start-Sleep -Milliseconds 400
    Remove-Item $runDir -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "first run created a usable certificate, verified to match, and a pairing key"

# Confirm the page really is inside the exe, so a release can never ship with
# the wrong page compiled in. Both paths get the full containment check: the
# exe's bytes have to contain all of web\index.html exactly as it stands now.
# The clean-compile path used to settle for a grep for 'Dictation Bridge', but
# the C# fallback stub a resource-less exe serves says those words too, so an
# exe with no page embedded passed it.
#
# Read the page as UTF-8 bytes, not with Get-Content. Under PowerShell 5.1 that
# cmdlet decodes a BOM-less file with the console's ANSI code page, so any
# non-ASCII character in the page comes out mangled and the comparison can never
# match -- which is a false alarm about the one check that exists to catch a
# stale exe. The exe's own bytes are decoded the same way, so both sides of the
# comparison are decoded alike.
$bytes = [System.IO.File]::ReadAllBytes($exe)
$text = [System.Text.Encoding]::UTF8.GetString($bytes)
$pageText = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($page))
if (-not $text.Contains($pageText)) {
    if ($UseExistingExe) {
        # A stale desktop\DictationBridge.exe copied over a freshly edited
        # web\index.html would otherwise assemble a folder whose exe serves an
        # older page, and every check above would pass: the page is embedded,
        # just not this one. This is the same reason AGENTS.md insists on
        # verifying the embedded page really changed after a rebuild.
        throw "the exe's embedded page is not web\index.html as it stands now; rebuild with .\build.ps1"
    }
    throw "the freshly compiled exe does not contain web\index.html as it stands now; the /resource embed did not take"
}
# The containment check is what makes -UseExistingExe safe: it is what proves
# the exe that got copied in is the one this page was compiled into, rather
# than whatever happened to be on disk.
if (-not (Select-String -Path $page -Pattern 'webkitSpeechRecognition' -Quiet)) {
    throw "the page has no speech recognition in it"
}
Write-Host "verified the page embedded in the exe is the current web\index.html"

# The QR encoder has to be in there too. All this proves is presence in the
# binary: it renders nothing and decodes nothing, so it is not proof that the
# code the panel shows will scan. The independent decode is a documented manual
# step instead -- the OpenCV procedure in CONTRIBUTING.md -- and that is the
# check that would have caught the hand-written encoder: it looked right and
# would not scan.
if ($text -notmatch 'QRCoder') {
    throw "the QR encoder does not appear to be compiled into the exe"
}
Write-Host "verified the QR encoder is compiled in"

# Nothing sensitive may reach the release folder. A .pfx is the certificate
# private key: publishing one lets anyone impersonate the certificate, and this
# package is meant to be downloadable. pairing-key.txt is the same kind of
# secret: whoever has it can type on the PC that made it.
$leaked = @(Get-ChildItem $outDir -Recurse -File -Include *.pfx, *.cer, pairing-key.txt -ErrorAction SilentlyContinue)
if ($leaked.Count -gt 0) {
    throw "the release must not contain keys or certificates: $($leaked.Name -join ', ')"
}

# The quick-start guide goes in beside the exe. The README is written as plain
# text so it opens by double-click with nothing installed. The screenshots are
# deliberately left out: they are served from the repository, and a folder
# someone unzips on a phone-carrying desk does not need them.
$readme = @'
# Dictation Bridge __DB_VERSION__

**Talk on your iPhone. The words appear in whatever window your Windows PC has focused.**

No typing, no leaning over the keyboard, no stopping what you're doing.

## Setup

**1. Unzip it first.**

Right-click the zip and choose Extract All, then run `DictationBridge.exe` from the
extracted folder. Running it straight from the zip preview makes Windows extract to a
temporary folder, and the certificate and pairing the app creates beside it are lost on
cleanup.

**2. Run `DictationBridge.exe`.**

That's the whole installation. No installer, no runtime, no setup wizard. A small
panel appears near the middle of your screen.

Leave it running. It needs to be running whenever you want to dictate.

**3. Get the certificate off the PC and onto the phone.**

The app just made you your own certificate. Right-click the panel and choose
Show the certificate file: Explorer opens with it selected. It is at:

    data\dictation-bridge.cer

Get that one file to your phone however is easiest. Email it to yourself and tap
it in the Mail app, or save it to iCloud Drive and tap it in the Files app.

It has to be *your* file, not someone else's. The app makes a certificate
covering your PC's addresses, and the phone has to trust that one.

Then on the phone:

1. Settings > General > VPN & Device Management
2. Tap the Dictation Bridge profile
3. Tap Install

**4. Give it permission.** Most people stop here and think the app is broken. There
is one more step:

4. Settings > General > About > Certificate Trust Settings
5. Switch on Dictation Bridge

**5. Open the page.** The panel shows a QR code. Point the phone's camera at it and
tap the notification that appears. That opens the page and pairs the phone with this
PC: the code carries a pairing key as well as the address. The address is written
beside the code, `https://192.168.1.162:17123/`, but typing it opens the page without
pairing it. If your camera will not read the code, use Copy pairing link on the panel
and send the link to the phone.

**6. Tap Start listening**, and allow the microphone.

## Use

Tap Start listening on the phone, then talk. Whatever you say is typed straight into the
window you are working in, a word at a time as you say it, not after you finish.

| | |
| --- | --- |
| Start / stop typing | Start listening, on the phone page |
| Know if it is working | The Start button is greyed out while it is, and the panel's dot breathes green |
| Stop typing | Tap Stop on the phone page |
| Anything else | Right-click the panel, or the tray icon -- same menu |
| Open the page on the phone | Scan the QR code in the panel. It pairs the phone too |
| Unpair every phone | Right-click the panel, Reset phone pairing..., scan the new code |
| Put the panel away | Right-click it, Hide the panel. Click the tray icon to get it back |
| Click the panel while you talk | Fine: it never takes the focus from your window |
| Move it | Drag it. It remembers where you left it |
| Find the certificate | Right-click the panel, Show the certificate file |
| Port already in use | Run `DictationBridge.exe --port 8099` |
| QR code has the wrong address | Run `DictationBridge.exe --address 192.168.1.20`, with the address the phone can reach |

To start the app with an option: hold the Windows key and press R, type the exe's full
path in quotes followed by the option, and press Enter -- for example
"C:\Where You Put It\DictationBridge.exe" --port 8099. Or right-click the exe, make a
shortcut, and add the option to the end of the Target field.

There is no pause on the PC and no queue. One thing worth knowing: if you leave the page
listening with the phone face up, it will pick up room noise and type it, so leave the
phone somewhere it cannot hear the room, or tap Stop when you are not using it.

Words are typed as they are recognised, less the last ten characters, which are held back
so that iOS can still correct itself. Most mishearings therefore never reach the screen. A
correction that lands further back than that cannot be undone: the word stays wrong, but it
is not typed twice and the sentence continues in order.

A locked screen stops dictation. While the page is listening it keeps the phone's screen
on (iOS 16.4 or newer), and lets it lock again when you tap Stop. If the screen still
locks while you dictate, set Settings > Display & Brightness > Auto-Lock to Never.

To update: replace the one exe and keep the `data\` folder beside it. Your certificate
and phone pairing survive, and a phone that still has the page open picks up the new
page by itself. If it is listening at that moment, it says so and waits for you to tap
Stop, so nothing you are saying is cut off.

Cantonese or Chinese: pick it from the dropdown on the phone page. Cantonese is the
default. If your iPhone rejects a language the page tries the next one and tells you
which it settled on.

## If something goes wrong

**The app just disappeared / never appeared.** The app writes everything it does to
data\dictation-bridge.log -- send that file with any problem report. If
data\last-run.txt still says state: running, it was killed or crashed; data\crash.txt
exists only when something went wrong inside the app -- send that too. If a red box
appeared instead, it names the problem; if it is about a port, start again with --port
and a different number.

**Windows protected your PC.** Windows shows this for any unsigned exe that came from
the internet. Click More info, then Run anyway. The app is a single unsigned file by
design; it has no installer.

**The panel says the certificate failed.** The app could not create its certificate --
the folder it runs from is probably read-only or protected (a corporate or system
location). Move the whole app folder somewhere normal, like your Desktop, and start it
again. Until then the phone page cannot use speech recognition.

"SpeechRecognition needs a secure (https) page" on the page: it was opened over plain
http, so Safari hides the recognizer. Open the https:// address written beside the QR
code instead. If the certificate is not on the phone yet, do steps 3 and 4 first; if
the panel says the certificate failed, see above -- there is no certificate to
install yet.

"This page is not a secure context" on the page: the certificate is not trusted yet.
Do step 4, which is a different place from step 3, then open the page again. Most
people stop after step 3 and think they are done.

"This browser has no SpeechRecognition" on the page: the iPhone is on iOS older than
14.5, or the page opened in a browser that cannot use speech recognition on an iPhone.
Update iOS, or open the page's address in Safari, and try again.

The page says only Safari can use speech recognition: it opened in another browser,
such as Chrome, because the Camera app opens a QR code in the default browser. Copy the
page's address into Safari, which keeps the pairing, or make Safari the default browser
and scan the code again.

The page says iOS did not allow speech recognition: iOS only runs it while Dictation or
Siri is on. Turn on Dictation in Settings > General > Keyboard, then tap Start listening.

"Not paired with this PC" on the page: it was opened without the pairing key, usually
by typing the address, or the pairing was reset since. Scan the QR code again. What you
said meanwhile waits on the phone and is typed once it pairs.

The panel says "new certificate: install it on the phone": the PC is on an address its
old certificate does not list, so it made a new one. Right-click the panel, choose Show
the certificate file, and install it on the phone as in steps 3 and 4. Then scan the QR
code. Each new certificate keeps the old addresses, so going back to a network the PC
has been on before does not ask again.

The panel says "new address: scan the QR code again": the PC moved to another address
the certificate already covers. Scan the code.

The page says the PC has not answered for a while: check the app is running and the
phone is on the same Wi-Fi as the PC, and look at the panel for one of the two messages
above.

The QR code has an address the phone cannot reach, on a PC with more than one network:
start the app with that address, `DictationBridge.exe --address 192.168.1.20`.

Nothing types: check the Start button on the phone page is greyed out, which means the
microphone is open and the page is sending, and that it says *connected to desktop*.

Nothing appears in the target program: the phone page says why. Usually the window
in front is running as administrator, which Windows does not let Dictation Bridge type
into, or the PC is locked. Click into the window you want and say it again; words that
were not typed are not sent again on their own.

Still stuck: on the phone page tap Send to desktop, then read
`data\diagnostics.log`. It records what the speech recogniser was doing.

## Requirements

- Windows 10 or later, 64-bit
- iPhone or iPad, iOS 14.5 or newer, Safari
- Both on the same network

## Privacy

Your speech is transcribed by Apple, because that is how Safari's dictation works.
The text goes to your own computer over your own network. Nothing is sent elsewhere.

Only a paired phone can type. The pairing key travels inside the QR code and the PC
never hands it out, so another device on the same WiFi gets the page but nothing it
sends is typed. The log holds neither the key nor what you said. Anyone who can see the
QR code, or has data\pairing-key.txt, has the key: never send that file with a bug
report, and use Reset phone pairing... to cut off a phone you no longer trust.

## Limits

The phone screen must stay on and unlocked. It cannot type into a program running as
administrator. If your PC's address changes to one the certificate does not list, the
phone installs a new certificate once.

## Licence

MIT. See LICENSE. The exe includes QRCoder, also MIT; see THIRD_PARTY_NOTICES.txt.

---

Not affiliated with Apple or Microsoft. Dictation on iOS is Apple's own feature; this
just forwards the resulting text to your PC.
'@
# The heading names the build a recipient is actually holding, so a report can
# say which version it came from. The here-string is single-quoted because its
# text carries backticks and quotes of its own, so $version -- the same value
# that chose the folder above -- is stamped in through a marker rather than by
# interpolation.
$readme = $readme.Replace('__DB_VERSION__', $version)
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
