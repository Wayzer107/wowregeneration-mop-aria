WoW Regeneration (aria2) - Mists of Pandaria
=============================================

Downloads a complete, playable World of Warcraft: Mists of Pandaria 5.4.8 (build 18414) client from
Blizzard's CDN: the MPQ game data plus the Windows client files (`Wow.exe`, `Wow-64.exe`, their
helpers and `WoW.mfil`).

This is a modified version of [WoWRegeneration](https://github.com/The-Cataclysm-Preservation-Project/wowregeneration),
ported from 4.3.4 to 5.4.8. Downloads go through [aria2](https://aria2.github.io/) with 16 connections
per file, and the project targets .NET 11.

## Where the files come from

- The manifest `wow-18414-447E3E618F731CCBF4F7D2C4E56C5644.mfil` on
  `dist.blizzard.com.edgesuite.net/wow-pod-retail/NA/15890.direct/` lists the 5.0.3 (15890) base
  install plus every update to 18414. Only the NA path still serves it; EU returns 404.
- The manifest is version 3, so files are chosen by their tags: all `base`/`EXPn` data and
  `alternate.MPQ`, the chosen locale's MPQs, and the chosen OS's `base-<OS>.MPQ` and
  `Updates/wow-0-18414-<OS>-final.MPQ`. That is 56 files and about 23.4 GB for one locale on Windows.
- The manifest has no loose executables. They are built from two MPQs it does contain:
  `Data/base-Win.MPQ` holds the 5.0.3 install files, and `Updates/wow-0-18414-Win-final.MPQ` holds
  newer files plus binary patches (Blizzard PTCH/BSD0) that turn the 15890 `Wow.exe`, `Wow-64.exe`
  and `MovieProxy.exe` into their 18414 versions. The program reads these MPQs itself (a small
  built-in MPQ v4 reader with zlib and LZMA) and applies the patches. Each of the 21 resulting
  files is checked against its known MD5; the patched executables are Blizzard-signed 5.4.8.18414.
  `Battle.net.dll`, `msvcr80.dll` and `WowError.exe` are left out because the update deletes them.
- `WoW.mfil` is written read-only, pointing at the NA manifest.

## Changes from the original

- Each file is downloaded by `aria2c` with 16 connections (`--split=16`), instead of a single
  `WebClient` connection.
- Interrupted downloads resume. That includes a half-finished file left behind by the original
  WebClient version, which `aria2c --continue` appends to instead of starting over.
- A file is only recorded as complete when `aria2c` reports success. If a download fails, the
  program stops, and running it again resumes the session.
- Ported from .NET Framework 4.0 to .NET 11 (SDK-style project, `HttpClient` for the manifest).
- Downloads 5.4.8 instead of 4.3.4, and builds the Windows client files from the downloaded MPQs
  when the Win OS is chosen. Files that are already correct are skipped. A file with the wrong MD5
  is kept as `name_BACKUP` and built again.

## Requirements

- [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0) to build, or the .NET 11 runtime to run a build.
- `aria2c` next to `WoWRegeneration.exe` or on your `PATH`
  (Windows: `scoop install aria2` or `winget install aria2.aria2`; Linux: your package manager's `aria2` package).

## Build

```
cd Source
dotnet build -c Release
```

The program is written to `Source/WoWRegeneration/bin/Release/net11.0/`.

## Usage

Run the program from the folder you want the client downloaded into, and answer the prompts for
locale and OS. The client goes into a `WoW-18414` folder, and progress is kept in `session.xml`, so
you can stop at any point and pick up where you left off by running it again. When it finishes,
`WoW-18414` holds the complete client, ready to run with `Wow.exe` or `Wow-64.exe`.

```
WoWRegeneration [--connections N] [--aria2c PATH]
```

| Option | Default | Meaning |
|---|---|---|
| `--connections N`, `-x N` | 16 | Connections per file, 1 to 16 (aria2c's maximum). |
| `--aria2c PATH` | search | Path to `aria2c`. Without it, the program looks next to itself, then on `PATH`. |

## Credits

Original WoWRegeneration by valcriss, with later work by Yaki Khadafi, Dramacydal and funjoker
(The Cataclysm Preservation Project). The original repository has no license file.

The MPQ reader, PTCH/BSD0 patcher and LZMA decoder are new implementations, written from the
formats as documented by StormLib and the LZMA specification (LzmaSpec, public domain).
