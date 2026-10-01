# Contributing

This project is small on purpose: one page of HTML and one C# file, built with the
compiler that already ships with Windows.

## Build

```powershell
.\build.ps1
```

That compiles with the `csc.exe` in `Microsoft.NET\Framework64\v4.0.30319` and embeds
`web/index.html` into the exe as a resource. No SDK to install and no network access
needed.

It also compiles in the vendored QRCoder encoder under `vendor/QRCoder/`, which supplies
the QR code on the panel. That is the only third-party code in the project, it is MIT,
and it is compiled rather than loaded, so the exe is still a single file. See `AGENTS.md`
for why it is vendored instead of written by hand.

Edit `web/index.html`, rebuild, and copy the exe to wherever you are testing. The build
stops a running copy of the app first, because Windows will not let you overwrite a file
that is executing.

## Read this first

**[AGENTS.md](AGENTS.md)** documents the conventions and the traps: why the recognizer must
be retired before it is aborted, why WebSocket came out in favour of HTTP, why the
certificate is self-signed, and which two WinForms properties will crash the app at
startup. Nearly every bug found in this project came from one of those.

## Verifying a change

There is no test framework and adding one is not worth it at this size. Verify by running
things:

```powershell
# does it build and start
.\build.ps1
.\desktop\DictationBridge.exe --port 8099

# do the endpoints behave
node -e "const https=require('https');https.get({host:'127.0.0.1',port:8099,path:'/config',rejectUnauthorized:false},r=>{let b='';r.on('data',d=>b+=d);r.on('end',()=>console.log(r.statusCode,b.trim()))})"
```

For the web page, drive real Chrome over the DevTools Protocol:

```
chrome --headless=new --ignore-certificate-errors --remote-debugging-port=9333
```

Stub `window.SpeechRecognition` before the page script runs. That is the only way to
exercise the language fallback chain and the idle-then-speak path without an iPhone in
hand. Always use a real browser rather than a hand-rolled client: browsers take header
values verbatim and trim them, so a lenient client will pass where Safari fails.

`--ignore-certificate-errors` is required, because the certificate is self-signed.

### The QR code

The panel's QR code is the one thing that has to be verified with an **independent**
decoder, because a QR code that renders convincingly can still be unreadable. The first
version here was a hand-written encoder and no scanner could read it.

Screenshot the panel and feed the crop through OpenCV:

```python
import cv2
img = cv2.imread(r"docs/panel-expanded.png")
print(cv2.QRCodeDetector().detectAndDecode(img)[0])
```

The crop must match `https://<ip>:<port>/` as printed on startup. If OpenCV cannot read
it, the phone will not either. `make-release.ps1` at least checks the encoder is present
in the binary, which catches a release built without the vendored source.

## Release

```powershell
.\make-release.ps1
```

Produces `release\DictationBridge-<version>\` containing two files: the exe with the page
and the QR encoder compiled in, and a `README.txt` for whoever unzips it.

**No certificate travels with the release.** The app mints one on the recipient's first
run, in `data\`, and their `README.txt` points them at their own file. This is deliberate:

- the certificate is machine-specific. Its SAN lists the addresses of whichever PC
  created it, so one built on a different machine would not cover the recipient's
- a `.pfx` is the private key. Publishing one with a download means anyone can
  impersonate that certificate

The script **verifies two things that would otherwise ship broken**: that the page really
is embedded in the exe, and that a first run in an empty directory creates a `.cer` and a
`.pfx` that match each other. It also fails the build outright if any `.pfx` or `.cer`
ends up in the release folder, so a regression cannot quietly reintroduce a key.

The screenshots are not copied. `docs\` is served from the repository, and a folder
someone unzips next to their phone does not need them.

`release\` is gitignored, and now contains nothing that should be published by accident.

## Documentation

`README.md` is for people using the app. Keep it in setup steps, troubleshooting and
limits. Anything that only matters when editing the code belongs here or in `AGENTS.md`
instead.

Three READMEs are kept in step: English, Traditional Chinese and Simplified Chinese. If
you change one, change all three.

## Reporting a problem

A report is far more useful with the diagnostics log attached. On the phone page, tap
**Send to desktop**, then attach `data/diagnostics.log`.

It records recognizer events and error codes with timestamps. It does not record what
anyone said unless the *include spoken words* option was turned on, which is off by
default.

## Security

Treat this as a personal tool on a trusted LAN. The token in `/config` is the only
barrier, and it travels over TLS to whoever loads the page. Do not describe it as secure,
and do not add features that assume it is.

`data/dictation-bridge.pfx` holds the certificate private key and is gitignored. Never
commit it, and never commit anything from `data/`.

Before committing, check for secrets in the staged blobs rather than the working tree:
the phone token, the Serper key used for research, and any certificate.
