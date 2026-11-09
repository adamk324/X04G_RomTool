# Development notes

## Build

| What | Command | Where |
|---|---|---|
| Everything (Windows) | `dotnet build X04G_Windows_Toolkit.sln -c Release` | Windows, .NET 8 SDK |
| Tests | `dotnet run --project tests\X04G.Core.Tests -c Release` | Windows or Linux |
| Portable exe | `.\build.ps1` (or the `dotnet publish` line in README) | Windows |
| Core + CLI + tests only | `dotnet build src/Cli` / `dotnet run --project tests/X04G.Core.Tests` | Linux/macOS (the WinForms project needs the Windows Desktop SDK) |

No NuGet packages are used anywhere — not even a test framework (`tests/X04G.Core.Tests/TestFramework.cs`
is a ~80-line runner). The solution builds offline with only the .NET 8 SDK.

CI: `.github/workflows/build.yml` builds and tests on `windows-latest`, publishes `X04GToolkit.exe` as an
artifact, and runs the core tests on Ubuntu with `simg2img`/`img2simg` and e2fsprogs installed so the
cross-checks against the reference tools run too.

## Architecture

```
Application (WinForms, net8.0-windows)   Cli (net8.0)
          \                               /
           └──────────── Core (net8.0) ──┘
   Common            OperationLog, SafeFile (read-only / create-new), Findings & Verdict
   ImageProcessing   SparseReadStream / SparseImageWriter / RawImageWriter
                     LpMetadataReader / LpMetadataWriter / LpLayoutPlanner / SuperImage / SuperImageBuilder
                     Ext4Image (read-only), FileSystemDetector, BootImage, Ext4Toolchain (WSL / native e2fsprogs)
   RomProfiles       RomProfileCatalog (4 options), AndroidImageInspector, SystemCompatibilityChecker
   StorageAnalysis   StorageAnalyzer → StorageReport (PASS/WARNING/FAIL)
   Preparation       PreparationRequest → PreparationPlanner (plan shown for confirmation) → PreparationExecutor
```

Flow: the wizard builds a `PreparationRequest`; `StorageAnalyzer.Analyze` (read-only) gives a `StorageReport`;
`PreparationPlanner.Plan` lists every file read/created/deleted; `PreparationExecutor.Execute` re-validates,
then extracts / shrinks (copies) / builds / verifies / patches boot / writes report + checksums, or simulates
all of that in a dry run.

The UI never contains image logic, so everything that matters is covered by tests without a GUI.

## Technical discoveries

* **LP metadata version.** Android 10's liblp only accepts metadata 10.0 (`LP_METADATA_MINOR_VERSION_MAX` is 0 in
  android10-release; 10.1/10.2 came with Android 11). The toolkit reads 10.0–10.2 but always writes 10.0 with a 128-byte header.
* **lpmake layout rules** (builder.cpp): reserved area = 4096 + (4096 + metadata_max × slots) × 2, aligned up to the
  block-device alignment (lpmake default 1 MiB) → `first_logical_sector = 2048` for the scripts' 64 KiB/1 slot. Partitions
  are allocated in command-line order from free regions whose starts are aligned to 1 MiB; partition sizes are rounded up
  to the 4 KiB logical block. The storage checker runs this exact allocator, so gaps between partitions are accounted for.
* **lpmake sparse output** (images.cpp): reserved 4 KiB as FILL, geometry ×2 + metadata ×slots×2 as RAW, then each image
  block-by-block — a 4 KiB block whose 32-bit words are all equal becomes FILL, else RAW; unused space is DONT_CARE.
  `SuperImageBuilder` mirrors that. Our sparse output expands with AOSP `simg2img` to exactly the raw output (tested).
* **Reproducibility.** Repacking an unmodified super with the parameters read from it reproduces a byte-identical raw image (tested).
* **Group size.** The scripts set group `main` to the sum of the images. The toolkit keeps the source group's maximum when
  it is large enough and only raises it (to the sum, like the scripts) when needed, logging the change.
* **resize2fs -M truncates image files**, which is why the scripts can `stat` the size afterwards. The toolkit additionally
  trims the file to the superblock's block count and refuses to continue if it is shorter.
* **shared_blocks.** ext4 images built with block deduplication (`EXT4_FEATURE_RO_COMPAT_SHARED_BLOCKS`) cannot be
  resized by resize2fs; they are used as-is and reported as not shrinkable.
* **Third-party lpunpack.py** (unix3dgforce) treats sparse FILL chunks as zeros, so it mis-expands sparse supers that
  contain non-zero fills. The cross-check test therefore uses it only for raw images; sparse output is checked against `simg2img`.
* **Boot cmdline patch.** In boot header v0–v2 the SHA-1 `id` does not cover the cmdline, so changing only the 512-byte
  cmdline field is a complete, reversible edit; the AVB footer no longer matches, which is why the documented flash uses
  an empty vbmeta with verification disabled.
* **ext4 reading.** Extents, classic block maps (incl. indirect blocks), inline data (≤ 60 bytes), fast and slow symlinks
  and htree directories (read linearly) are supported. `meta_bg` filesystems are detected (statistics only).

* **Android 16 MP2 boot blocker (X04G).** Android 16's `libmedia_codeclist_capabilities` (`AudioCapabilities.cpp`, built
  with `unsigned-integer-overflow` UBSan) has no limits for `audio/mpeg-L2`. With no `channel-count` limit in the vendor
  XML, `parseFromInfo()` sets the channel range to `[0,0]`, the intersection is empty, and
  `getMaxInputChannelCount()` evaluates `mInputChannelRanges.size() - 1` on 0 → `ubsan: sub-overflow` abort inside
  `system_server` (StorageManagerService.handleSystemReady → codec list). Reproduced by compiling the unmodified Android 16
  source with Android's sanitizer flags. Only MP1/MP2 reach that code on Android-10 vendors: the VNDK-29 OMX store drops
  types without an OMX role (WMA, APE, ALAC, ADPCM). `Mp2CodecFix` generates the corrected file from the vendor; on the
  real X04G vendor it produces exactly the file that was flashed and booted (SHA-256 `6098df03…58c4`, 5075 → 4759 bytes).

## Implemented

* ROM selection screen with the four options and their documentation status.
* Storage checker: LP geometry/metadata parsing with checksum validation and backup-copy fallback, current
  allocation, exact simulated layout, alignment overhead, shrink lower bound, estimated output size, Windows drive free space.
* GSI/system compatibility checks from image properties (filesystem, SAR layout, ABI, SDK, VNDK, Treble).
* Unpack super.bin / super.img (raw or sparse), build super (sparse or raw) with replaced/removed partitions,
  product removal for GSI, optional ext4 shrink on copies, documented boot cmdline patch, verification, dry run,
  cancellation (`*.incomplete` markers), unique run folders, logs, reports, SHA-256 checksums.
* Known fixes (`mp2-codec`) with detection, recommendation by system SDK, generated patches and diff preview.
* In-place ext4 file replacement (`Ext4FilePatcher`), integrated in analysis, plan, executor, GUI and CLI.
* WinForms 9-step wizard, CLI, 40 automated tests.

## Not implemented / known limitations

* **The WinForms application was compile-checked but not run during development.** It was developed on Linux, where the
  Windows Desktop SDK is unavailable: the GUI sources were type-checked against Mono's System.Windows.Forms (the only error
  was `FolderBrowserDialog.UseDescriptionForTitle`, which exists in .NET 8 WinForms). The real build and a visual check
  must happen on Windows (`build.ps1` or CI). The Core library, CLI and all tests were built and run.
* No in-place editing of files inside ext4 images on Windows (would need an ext4 writer). Use extracted images + WSL, then select them as replacements.
* Shrinking needs WSL + e2fsprogs or e2fsprogs Windows binaries (not bundled).
* EROFS images are accepted but not inspected; no EROFS/F2FS shrinking.
* Retrofit (multi-device) super layouts and virtual A/B (metadata 10.2 features such as `updated` attributes) are not written.
* LX04 is analysis-only. No conversion between the LX04 and X04G partition layouts.
* No device communication, no flashing — by design.
* Compatibility checks cannot tell whether a GSI boots; only structural and property checks are possible offline.
* Tested with synthetic images only. No real X04G backup was available, so no X04G-specific numbers are hard-coded;
  everything is read from the user's image.

## Testing results (2026-11-08, Linux x64, .NET 8.0.133)

```
40 passed, 0 failed, 0 skipped      (2026-11-09; 32 on 2026-11-08)
```

Added on 2026-11-09: in-place patch keeps inode/blocks/SELinux label and changes only the expected blocks (checked with
e2fsck and debugfs); unsafe patches refused; GSI build with a vendor file patch on an X04G-like super (2 metadata slots,
512-byte alignment); MP2 detection, Android-16-only recommendation, channel-limited and mixed-type codecs; analysis
FAIL until the fix is enabled; end-to-end GSI build with the fix.

Covered: sparse round trip, independent `img2simg`/`simg2img` cross-checks, corrupt/truncated/empty sparse images;
lpmake layout rules, insufficient space, exact fit, group limits, metadata round trip and checksums, backup-copy
fallback, destroyed metadata, cross-check with lpunpack.py; ext4 reading (extents, block maps, inline data, symlinks),
filesystem/archive detection; boot cmdline patch (only the field changes, no overwrite, unknown cmdline refused);
analysis PASS for stock, GSI fit after product removal (compatibility never PASS), insufficient storage (WARNING with
shrink hint, execution stops before writing), FAIL for old non-SAR GSI, invalid inputs (not super, missing file, gzip,
no system, no vendor), truncated super, LX04 analysis-only, host disk reporting; execution with verification and
untouched originals, second run never overwrites, byte-identical stock repack, dry run writes nothing, missing toolchain,
shrinking with real e2fsprogs to make a tight GSI fit, cancellation.

Manual scale test: 3 GiB super with a 2 GB sparse GSI → 192 MiB sparse output written in ~10 s and verified in ~9 s;
the output expanded with `simg2img` parses to the expected layout.

A successful build or passing test does not show that an image works on the physical device.
