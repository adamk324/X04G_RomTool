# Changelog

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
