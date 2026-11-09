# Changelog

## 2.1.0 — 2026-11-09 — Known fixes and in-place file patching

### Added
* **Known fix "mp2-codec" (Android 16 boot fix for MediaTek vendors).** The analysis reads the vendor's codec list
  (`/vendor/etc/media_codecs.xml` and its includes) and detects MPEG audio Layer I/II decoders (`audio/mpeg-L1`/`-L2`)
  without a channel-count limit. Android 16's native `AudioCapabilities` aborts `system_server` on those
  (`ubsan: sub-overflow` while building the codec list), so boot never completes. When the selected system is
  Android 16 or newer (SDK ≥ 36), the report shows a **FAIL "Known boot blocker"** and the fix is pre-selected;
  the toolkit generates the corrected XML from *your own* vendor file (only the affected `<MediaCodec>` element is
  removed, every other byte kept) and applies it in place. Verified on a Mi Smart Clock X04G: Android 16 boots, audio works.
  GUI: *Preparation options → Known fixes for your vendor* (with *Show the exact change…*). CLI: `--fix mp2-codec`,
  `check-fixes --vendor <img> --sdk <n>`.
* **In-place file replacement inside ext4 partition images** (`Ext4FilePatcher`): same inode, blocks, owner, mode,
  timestamps and SELinux label; refuses metadata_csum, journal recovery, unclean filesystems, inline data,
  uninitialized extents, holes and block-count changes; verified by re-reading the file and comparing both images
  block by block. GUI: *Advanced: replace a file inside a partition*. CLI: `patch-file`, `build --patch part:/path=file`.
* Run folders keep `work/<partition>.orig.img` (unmodified reference), `work/<partition>.patched.img` and
  `work/generated/<file>` for every patch.
* The confirmation page now also requires acknowledging compatibility FAILs that do not block building.


## 2.0.0 — 2026-11-08 — Windows toolkit

Complete rewrite of x04g_tools as a Windows application. The original Linux scripts and guides are preserved in
`tools/legacy-linux/` and `documentation/original/`.

### Added
* WinForms wizard (`X04GToolkit.exe`, .NET 8) with 9 steps: welcome, ROM selection, input files, analysis,
  compatibility report, options, confirmation, execution, results.
* ROM selection: X04G Global (Android Go 10, documented), LX04 Chinese (Android 8.1, not documented — analysis only),
  GSI (documented, experimental per image), custom system.img (experimental).
* Native replacements for `lpunpack`, `lpmake` and `simg2img` (LP metadata 10.0, lpmake allocation rules, sparse read/write).
* Storage checker with PASS / WARNING / FAIL, exact layout simulation, alignment overhead, shrink lower bound,
  output size estimate and Windows drive free-space check.
* GSI/system compatibility checks from build.prop and image layout (SAR, ABI, SDK, VNDK, Treble).
* Read-only ext4 reader (extents, block maps, inline data, symlinks).
* Documented boot cmdline patch (SELinux permissive) without Android Image Kitchen.
* Optional ext4 shrinking (e2fsck + resize2fs -M) via WSL or e2fsprogs binaries, on working copies only.
* Dry run, verification of every output, unique run folders, `operation.log`, `report.txt`, `SHA256SUMS.txt`.
* `x04g-cli` (info / analyze / build / unpack / lpmake).
* Zero-dependency test suite (32 tests) with synthetic ext4 fixtures; GitHub Actions workflow.

### Changed (compared with the scripts)
* Outputs never overwrite anything (the scripts always wrote `NEW_super.img` / `super.new.img`).
* Intermediate files are kept (the scripts deleted the unpacked images, `mnt/` and `temp-super-temp.img`).
* Source metadata parameters (metadata size, slots, alignment, block size, group) are read from the input instead of hard-coded.
* No root, loop mounts or Linux needed.

### Removed
* In-place editing of mounted images (not possible on Windows without an ext4 writer).
