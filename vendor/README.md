# vendor

Third-party source, compiled into `DictationBridge.exe` by `build.ps1`.

## QRCoder

- Upstream: https://github.com/codebude/QRCoder (tag `v1.4.3`)
- Licence: MIT, see `QRCoder/LICENSE.txt` — Copyright (c) 2013-2018 Raffael Herrmann
- Used for: the QR encoder behind the panel's `QrView`, and nothing else

Files taken are `QRCodeGenerator.cs`, `QRCodeData.cs`, `Exceptions/DataTooLongException.cs`,
`String4Methods.cs` and `Stream4Methods.cs`. The rendering, payload and structured-output
classes are not included, and `PayloadGenerator` is not vendored, so the four
`PayloadGenerator` overloads on `QRCodeGenerator` were removed rather than pulling in a
150 KB dependency this app would never call.

Three mechanical changes were made for the C# 5 level that `csc.exe` 4.0 enforces. None of
them alter encoding behaviour:

- one string interpolation became string concatenation, in `QRCodeGenerator.cs` and in
  `DataTooLongException.cs`
- getter-only auto-properties became fields on the private structs, since C# 5 will not
  assign to them from a struct constructor

This is the only third-party code in the project. Adding a second one needs the argument
made in `AGENTS.md` under "Layout rules".

## Where the notice goes

`LICENSE.txt` here is the authoritative copy. `make-release.ps1` reads it and writes
`THIRD_PARTY_NOTICES.txt` into the release folder, then fails the build if the text it
produced is not byte-identical to this file. Do not maintain a second copy of these words
anywhere: the notice only has to be correct once.