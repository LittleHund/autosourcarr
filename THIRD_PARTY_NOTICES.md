# Third-party notices

AutoSourcarr's own source uses the [MIT license](LICENSE). Dependencies, character
data and bundled tools retain their respective copyright and license notices.
NuGet versions and package content hashes are recorded in each project's
`packages.lock.json`.

| .NET dependency | Copyright / license | Upstream |
| --- | --- | --- |
| Microsoft.Data.Sqlite and Microsoft.Data.Sqlite.Core 10.0.13 | Microsoft Corporation; MIT | [EF Core repository](https://github.com/dotnet/efcore) |
| Microsoft.AspNetCore.TestHost 10.0.13 (development tests only) | Microsoft Corporation; MIT | [ASP.NET Core repository](https://github.com/dotnet/aspnetcore) |
| SQLitePCLRaw packages 2.1.13 | Copyright 2014–2024 SourceGear, LLC; Apache-2.0 | [SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw) |
| SQLite engine | Public domain; SQLitePCLRaw's package wrapper has its own notices | [SQLite copyright](https://www.sqlite.org/copyright.html) |
| Unidecode.NET 2.2.1 | Copyright 2020 Dima Stefantsov, Vitalii Ganzha, Sergey Kuznetsov; MIT | [Pinned package source](https://github.com/thecoderok/Unidecode.NET/tree/2c695e1520675cb3bba225d055adef3400c7ab09) |
| SharpCompress 0.50.6 | Adam Hathcock; MIT; incorporated Bouncy Castle notices retained | [Pinned package source](https://github.com/adamhathcock/sharpcompress/tree/542ed974d4fc276b59d378f6ae2c16eb7cafd38a) |
| Google.Protobuf 3.29.3 | Copyright 2008 Google Inc.; BSD-3-Clause (Grpc.Tools 2.69.0 runs protoc at build time only and is not redistributed) | [Protocol Buffers repository](https://github.com/protocolbuffers/protobuf) |
| Magick.NET-Q8-AnyCPU and Magick.NET.Core 15.0.0 | Copyright 2013–2026 Dirk Lemstra; Apache-2.0; ImageMagick Studio LLC and incorporated libraries retain their own licenses | [Pinned package source](https://github.com/dlemstra/Magick.NET/tree/79bb8700d7fe3a564f62926ab299fc2520cb2269) |

Unidecode.NET's publisher separately attributes its character tables to Python
Unidecode and the original Perl Text::Unidecode, and states that the tables are
distributed under the Perl license. Preserve that attribution with the package:
[upstream license notes](https://github.com/thecoderok/Unidecode.NET#license).

Matching character-property ranges use Unicode 15.0 data exposed by the reference
Python `unicodedata` module. Persistent proxy-identity keys use the retained Unicode
15 full case-fold mapping. Unicode character data is copyright Unicode, Inc. and
is covered by the [Unicode data terms](https://www.unicode.org/license.txt).
The full Unicode license is retained under `src/AutoSourcarr.Core/Unicode/Notices/`
and copied into the build/publish output with the character tables.

SharpCompress retains the existing BZIP2 and LZMA raw-ZIP formats alongside stored
and Deflate entries. Its license and incorporated Bouncy Castle notice are under
`src/AutoSourcarr.Core/Repair/Notices/` and copied to build/publish output.
Original archive names, CRCs, source paths and expanded sizes remain checked;
LZMA dictionary allocation is bounded before opening that codec.

Magick.NET's Apache license and full incorporated native-library notices are
retained under `src/AutoSourcarr.Core/Imaging/Notices/` and copied into
the build/publish output. The package contains the native codecs used for bounded
JPEG/PNG/WebP/GIF/AVIF/BMP analysis; no host image-tool setup is required. The app
installs its own raster-only resource/path/delegate policy. Linux native execution
still needs a Linux runtime check; Windows decode tests do not prove that platform.

The integrated image also contains third-party JVM, Android, browser, translation,
solver and repair tools. Their original license/copyright files accompany those
tools. The proposed repair guest additionally uses the Linux kernel and QEMU;
their GPL notices and corresponding source/build references must accompany any
distributed guest image. The current migration source checkpoint contains no
new compiled guest or QEMU distribution.
