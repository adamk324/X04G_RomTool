# X04G Windows ROM Toolkit

A portable Windows desktop application for **preparing and analyzing ROM images** for the
Xiaomi Mi Smart Clock (**X04G** global / **LX04** Chinese, MediaTek MT8167).

It replaces the Linux shell scripts of [octathorp/x04g_tools](https://github.com/octathorp/x04g_tools)
(`lpunpack`, `lpmake`, `simg2img`, loop mounts, `sudo`) with native C# code, wrapped in a 9-step wizard:

1. **Welcome** – target device and ROM type
2. **Select operating system** – X04G Global (Android Go 10), LX04 Chinese (Android 8.1), GSI, or custom `system.img`,
   each with Android version, target variant, documentation status, required inputs and storage notes
3. **Input files** – your own MTKClient `super.bin` (or a `super.img`), GSI / system image, `boot.bin`, output folder
4. **Analyze** – read-only inspection of LP metadata, ext4 filesystems and `build.prop`
5. **Compatibility report** – **PASS / WARNING / FAIL** for capacity and compatibility
6. **Preparation options** – extract, build, remove product, sparse/raw, optional ext4 shrinking, boot patch, dry run
7. **Confirmation** – exactly which files are read, created or deleted
8. **Execute** – progress bar, cancel, live log
9. **Results** – generated files with sizes and SHA-256, final report

> **This is not a flashing tool.** It never talks to the device. Flashing stays a manual step
> (see [documentation/original/FlashCustomImages.md](documentation/original/FlashCustomImages.md)).
> A successfully built image is structurally valid; that does **not** mean it boots.

## Safety guarantees

* Input files are opened **read-only**; nothing existing is ever overwritten (outputs use `FileMode.CreateNew`).
* Every run writes into a **new, uniquely named run folder** (`X04G_GSI_20261108_221500\…`).
* Processing never starts because a file was selected; you confirm a plan that lists every file first.
* **Dry run** reads all inputs, simulates the build in memory and reports the exact output size — and writes nothing.
* Every new super image is re-opened and **verified** (metadata + SHA-256 of every partition).
* Intermediate files are kept unless you explicitly ask for cleanup. Each run has `operation.log`, `report.txt`, `SHA256SUMS.txt`.
* PASS is only reported when every number comes from verified metadata. Anything uncertain is a WARNING.

## Download / build

Requirements: Windows 10 or 11 (x64). Building needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone <this repository>
cd X04G_Windows_Toolkit
.\build.ps1            # runs the tests and creates publish\X04GToolkit.exe (portable, self-contained)
```

or manually:

```powershell
dotnet run --project tests\X04G.Core.Tests                     # unit/integration tests
dotnet publish src\Application -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

`publish\X04GToolkit.exe` is a single portable file; no installer and no administrator rights are needed.
A command-line front end (`x04g-cli`) for scripting is in `src\Cli`.

### Optional: ext4 tools

Only the optional *Shrink ext4 images* step (what the original repack scripts did with `resize2fs -M`)
needs external tools. The toolkit finds them automatically in either place:

* **WSL** with e2fsprogs: `wsl --install`, then inside Ubuntu `sudo apt install e2fsprogs`
* `tools\e2fsprogs\e2fsck.exe` and `resize2fs.exe` next to the program (see [tools/e2fsprogs/README.md](tools/e2fsprogs/README.md))

Everything else works without them.

## What the ROM options mean

| Option | Android | Status | What it does |
|---|---|---|---|
| X04G Global ROM | Android Go 10 | **Documented** | Unpack / repack the stock super (system, vendor, product); optionally replace partitions with your own modified images |
| LX04 Chinese ROM | Android 8.1 | **Not documented** – analysis only | Inspects an LX04 image; no conversion or build (the repository describes no LX04 procedure) |
| GSI | from the image | **Documented, experimental per image** | Keeps vendor, drops product (documented), uses the GSI as system; optional SELinux-permissive boot cmdline |
| Custom system.img | from the image | **Not documented** – experimental | Like GSI, but product is kept unless you remove it |

**Known fixes.** For GSIs the toolkit checks your vendor for known boot blockers. Currently: MediaTek vendors that
declare an MP2 (`audio/mpeg-L2`) OMX decoder make Android 16 crash `system_server` during boot. The toolkit detects
this, pre-selects the fix for Android 16+ systems, and removes only that codec entry from your vendor while building
(verified on an X04G running Android 16). You can review the exact change before anything is written.

GSI compatibility is checked against **your** vendor and stock system: filesystem, system-as-root layout,
CPU ABI, Android SDK vs vendor, VNDK snapshot, `ro.treble.enabled`. A GSI is never reported as fully
compatible — booting cannot be verified offline.

## Repository layout

```
X04G_Windows_Toolkit/
  src/Application/      WinForms wizard (X04GToolkit.exe)
  src/Core/             all logic, no UI, no dependencies
    ImageProcessing/    sparse images, LP (super) metadata, lpmake-compatible allocator & writer, ext4 reader, in-place ext4 file patcher, boot image, ext4 toolchain
    StorageAnalysis/    storage checker and report
    RomProfiles/        ROM options, image inspector, GSI/system compatibility checks, known fixes
    Preparation/        request → plan → executor (dry run, verify, outputs)
  src/Cli/              x04g-cli
  tests/                zero-dependency test runner + synthetic ext4 fixtures
  tools/                legacy-linux (original scripts, for reference) · e2fsprogs (optional)
  documentation/        analysis of the original repository, user guide, original guides
  DEVELOPMENT.md        architecture, build, technical notes, limitations, test results
  CHANGELOG.md
```

## Credits

Based on the research, guides and scripts of [octathorp/x04g_tools](https://github.com/octathorp/x04g_tools)
and the XDA thread [Xiaomi Mi Smart Clock development guide (GSI)](https://forum.xda-developers.com/t/xiaomi-mi-smart-clock-development-guide-gsi.4629771/).
LP metadata and sparse formats follow AOSP `system/core/fs_mgr/liblp` and `libsparse` (android10-release).
External tools referenced by the guides: [MTKClient](https://github.com/bkerler/mtkclient),
Android Image Kitchen, Magisk, [GSI list](https://github.com/phhusson/treble_experimentations/wiki/Generic-System-Image-%28GSI%29-list).

The original repository has no license file; check with its author before redistributing.
