# Analysis of the original x04g_tools repository

Repository: https://github.com/octathorp/x04g_tools (latest commit inspected: `acec16d`).
Copies of the original guides and scripts are kept in [original/](original/) and [../tools/legacy-linux/](../tools/legacy-linux/).

## Contents

| File | Purpose |
|---|---|
| `README.md` | Device description (X04G = Android Go 10 / Google Assistant, LX04 = Android 8.1 / Xiao AI, same hardware: MT8167, 4× Cortex-A35 1.5 GHz, 1 GB RAM, 4" 800×480), links to MTKClient, AIK, Magisk, GSI list |
| `howtos/BackupFlash.md` | Full flash + per-partition backup with MTKClient GUI; backup ≈ 4 GB; no official X04G firmware exists; do not share backups (device IDs) |
| `howtos/UnpackRepackSuper.md` | Stock workflow with the `*_bin_*` / `*_img_*` scripts |
| `howtos/WorkingWithGSI.md` | GSI workflow: product+system removed, GSI as system, SELinux must be permissive via boot cmdline |
| `howtos/FlashCustomImages.md` | Bootloader unlock (MTKClient `e metadata,userdata,md_udc`, `da seccfg unlock`), fastboot flash boot/super/vbmeta (empty vbmeta, `--disable-verity --disable-verification`) |
| `tools/unpack_bin_super.sh` | `lpunpack super.bin`; grows system to 1 GiB, product and vendor to 512 MiB (`fallocate` + `resize2fs`); loop-mounts all three with `sudo` |
| `tools/repack_super_from_bin.sh` | unmount; `e2fsck -yf; resize2fs -M; e2fsck -yf` on each image; `lpmake --metadata-size 65536 --super-name super --metadata-slots 1 --device super:<size of super.bin> --group main:<sum> --partition system/vendor/product:readonly:<size>:main --sparse --output NEW_super.img`; deletes mnt/ and the images |
| `tools/unpack_img_super.sh` | `simg2img $1 temp-super-temp.img`, then like unpack_bin_super |
| `tools/repack_super_from_img.sh` | like repack_super_from_bin, device size taken from temp-super-temp.img; deletes it |
| `tools/unpack_gsi_super.sh` | simg2img + lpunpack; if product.img exists (stock super) deletes product.img **and** system.img, otherwise grows system to 2 GiB and mounts it; grows/mounts vendor |
| `tools/repack_gsi_super.sh` | shrink system + vendor, `lpmake … --partition system … --partition vendor … --sparse --output super.new.img` |

## How the super workflow works

1. `super.bin` is a raw dump of the super partition (Android 10 dynamic partitions, LP metadata format).
2. `lpunpack` extracts each logical partition (system, vendor, product) as an ext4 image.
3. The images are enlarged so files can be added while loop-mounted (needs root).
4. Before repacking, each image is fsck'ed and shrunk to its minimum size (`resize2fs -M`, which also truncates the file).
5. `lpmake` writes a new LP metadata table (64 KiB, 1 slot, group `main` with max = sum of images, partitions read-only)
   for a block device of exactly the original size, and the images, as a sparse image for fastboot.

## Linux dependencies and how the toolkit replaces them

| Linux dependency | Used for | Windows toolkit |
|---|---|---|
| `lpunpack` | read LP metadata, extract partitions | **Native** (`LpMetadataReader`, `SuperImage`) incl. checksum validation and backup copies |
| `lpmake` | new LP metadata + image | **Native** (`LpLayoutPlanner` = AOSP MetadataBuilder allocation rules, `LpMetadataWriter`, `SuperImageBuilder` = AOSP ImageBuilder) |
| `simg2img` | sparse → raw | **Native**, without temporary files (`SparseReadStream` exposes the expanded image) |
| sparse output (`lpmake --sparse`) | fastboot-ready image | **Native** (`SparseImageWriter`) |
| `mount -o loop`, `sudo` | edit files inside images | Not needed for the workflows. Editing files inside ext4 images is not offered on Windows; extracted images can be edited elsewhere (e.g. WSL) and selected as replacements |
| `fallocate` + `resize2fs <size>` | grow images for editing | Not needed (no mounting) |
| `e2fsck`, `resize2fs -M` | shrink before repack | **Optional**, through WSL or e2fsprogs `.exe` files, always on working copies. Re-implementing an ext4 resizer was judged too risky |
| `stat` | sizes | .NET |
| Android Image Kitchen (boot cmdline) | SELinux permissive | **Native** header-only patch (`BootImage`) |

**Decision:** native C# for everything except ext4 resizing. WSL2 as a general backend was rejected because it
would make the core workflows depend on WSL being installed, configured and able to access the files; it is
used only for the optional shrink step.

## Safe vs special handling

* **Safe natively:** reading metadata/filesystems, extraction, building new images, sparse conversion,
  boot cmdline patch (new copy), verification — none of these modify inputs.
* **Special handling:** shrinking (external tool, modifies data) → only on copies in the run folder, with e2fsck before and after,
  exit codes checked, file trimmed to the filesystem size, final layout re-checked before writing.
* **Not reproduced on purpose:** the scripts' destructive behaviour — overwriting `NEW_super.img`, deleting the
  unpacked images and `temp-super-temp.img`, `rm -rf mnt`, deleting product/system in `unpack_gsi_super.sh`.

## Information that is *not* in the repository (and therefore not invented)

* The size of the X04G super partition and of its logical partitions. The toolkit reads them from your own image.
* Any procedure for LX04 ROMs or for converting between LX04 and X04G layouts.
* A list of GSIs known to work. Compatibility is checked per image from its properties only.
