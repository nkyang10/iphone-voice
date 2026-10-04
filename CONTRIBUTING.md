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

**`.\build.ps1` always refreshes `release\DictationBridge-1.0.0\`**, so the exe you copy to
another machine is the same one you just built — byte for byte, worth checking with
`Get-FileHash` after a build. It does this by calling `make-release.ps1 -UseExistingExe`,
which assembles the folder from the built exe without compiling again but still runs every
check, including that the exe's embedded page is the `web\index.html` that exists right now.
It also wipes the folder first, so a `data\` directory left behind by running the app from
there — which contains a certificate private key — can never survive into a package.

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
.\build.ps1                 # what you normally want: builds, then refreshes the folder
.\make-release.ps1          # compile again from scratch, into the folder
.\make-release.ps1 -UseExistingExe   # assemble from desktop\DictationBridge.exe
```

`build.ps1` already does the last one, so running `make-release.ps1` by hand is only for
inspecting or rebuilding the folder without touching `desktop\`. Both produce
`release\DictationBridge-<version>\` containing four files:

| | |
| --- | --- |
| `DictationBridge.exe` | The whole program. Page and QR encoder compiled in. |
| `README.txt` | Setup steps for whoever unzips it. Plain text, opens on double-click. |
| `LICENSE` | This project's MIT licence. |
| `THIRD_PARTY_NOTICES.txt` | QRCoder's MIT notice, copied in verbatim. |

**No certificate travels with the release.** The app mints one on the recipient's first
run, in `data\`, and their `README.txt` points them at their own file. This is deliberate:

- the certificate is machine-specific. Its SAN lists the addresses of whichever PC
  created it, so one built on a different machine would not cover the recipient's
- a `.pfx` is the private key. Publishing one with a download means anyone can
  impersonate that certificate

The licence files are not optional extras. QRCoder is MIT, which permits redistribution
only if the copyright notice travels with the copies, and the exe *is* a copy in the
sense that matters. `THIRD_PARTY_NOTICES.txt` is generated by concatenating the upstream
`vendor/QRCoder/LICENSE.txt` rather than retyping it, and the build fails if the
reproduced text is not byte-identical to the original — a hand-transcribed licence drifts.

The script **verifies four things that would otherwise ship broken**: that the page is
embedded in the exe, that the QR encoder is present, that a first run in an empty
directory creates a `.cer` and a `.pfx` that match each other, and that no `.pfx` or
`.cer` ends up in `release\`. That last one means a future edit cannot quietly
reintroduce a key.

The screenshots are not copied. `docs\` is served from the repository, and a folder
someone unzips next to their phone does not need them.

`release\` is gitignored, and now contains nothing that should be published by accident.

## Publishing a release

**Do not publish unless you were asked to.** Build the exe and stop there. `.\build.ps1`
for a binary to test, `.\make-release.ps1` for the folder, and that is the end of it. The
user tests a local build and decides when something is worth putting in front of anyone
else, which is a decision that belongs to them and not to a build step. Committing and
pushing are the same: asked for, not assumed.

Replacing a published asset is not free either. GitHub cannot overwrite one, so it is
delete-then-upload, and there is a window where the asset does not exist at all. Doing that
unprompted, repeatedly, is how a release ends up briefly broken for anyone following it.

There is no automation for publishing, and the first attempt at it was a reminder of why.
`Invoke-WebRequest` in PowerShell 5.1 parses responses with the IE engine and throws a
`WebException` with a **null `Response`** when a request *succeeds*. That reads exactly
like a failure. Assuming it had failed and retrying created a duplicate release, and a
diagnostic `POST` created a third.

So:

- Check the resulting state with a `GET` before retrying anything. `GET /releases`
  includes drafts when authenticated, so a `GET` is a safe probe and a `POST` is not.
- Use `curl.exe`, which reports status plainly, for anything that mutates.
- Never use a mutating call to read an error message.
- Take the token from the git credential helper, never from a prompt or a literal.

The token lives in the git credential helper (`credential.helper=manager`), so no token
needs to be pasted into a shell, a script, or a commit. `gh` is not required and is not
installed.

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
