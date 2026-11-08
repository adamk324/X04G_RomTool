# User guide

## 1. Make a backup first

Follow [original/BackupFlash.md](original/BackupFlash.md): MTKClient → *Read flash*, *Read preloader*, *Read boot2*,
then *Read partition(s)* with all partitions selected. You get `super.bin`, `boot.bin`, `vbmeta.bin` … Keep these
files untouched. The toolkit only reads them, but keep a second copy anyway.

## 2. Stock ROM: unpack, modify, repack (X04G)

1. Welcome: *X04G*, *Stock ROM* → Select OS: *Xiaomi X04G Global ROM*.
2. Input files: select `super.bin`. Leave the replacement fields empty for now.
3. Report: shows the super size, the metadata area, each partition, and PASS for the current layout.
4. Options: tick *Extract all partitions* (untick *Build* if you only want to unpack). Execute.
   The run folder contains `extracted\system.img`, `vendor.img`, `product.img`.
5. Modify the extracted images with tools of your choice (for example in WSL:
   `sudo mount -o loop system.img /mnt/x` — enlarge first with `truncate`/`resize2fs` if you need room).
6. Start over, select the same `super.bin`, and select your modified images as *Replacement system/vendor/product*.
   Enable *Shrink ext4 images* if ext4 tools are available (this is what the original repack scripts did).
7. The new `super_X04G_new.img` is written to a new run folder and verified.

## 3. GSI

1. Select OS: *Generic System Image (GSI)*.
2. Input files: base super (`super.bin`, or a super.img you built and know boots), the GSI `.img`
   (decompress `.xz` first), and optionally your `boot.bin`.
3. The report checks capacity (vendor + GSI, product removed as documented) and compatibility with your vendor.
   A typical result is capacity PASS and compatibility WARNING — GSIs can never be fully verified offline.
   If capacity is WARNING "could fit only if shrunk", enable shrinking (needs ext4 tools).
4. Options: keep *Remove the product partition* (documented), *Sparse*, *Verify*, and *Create a boot image with SELinux permissive*.
5. Run a **dry run** first if you like; it reports the exact output size without writing anything.
6. Flash manually following [original/FlashCustomImages.md](original/FlashCustomImages.md):
   `fastboot flash boot boot_selinux_permissive.img`, `fastboot flash super super_GSI_new.img`,
   `fastboot --disable-verity --disable-verification flash vbmeta vbmeta.img` (MTKClient's `vbmeta.img.empty`).

## 4. Reading the report

* **PASS** – the planned layout was simulated with the same allocator as `lpmake`, using the metadata
  parameters read from your own super image, and it fits. For compatibility rows: the individual check passed.
* **WARNING** – something cannot be verified (for example the size after shrinking, missing properties, EROFS images,
  or whether a GSI boots). You must acknowledge warnings before executing.
* **FAIL** – does not fit, or an input is invalid (not a super image, compressed archive, truncated file,
  older Android than the vendor, mismatched layout …). Execution is blocked.

Numbers shown: total super size, LP metadata reserved area (geometry + 2 × metadata slots, aligned),
usable space, current allocation, required space (including 1 MiB alignment gaps), remaining space,
estimated output file size, and free space on the Windows output drive.

## 5. Troubleshooting

| Message | Meaning |
|---|---|
| *not a super image (no logical partition metadata at offset 4096)* | You selected a partition image or another file. Use `super.bin` from the partition backup. |
| *Image length differs from the super size recorded in the metadata* | The file is truncated or padded. Do not build from it. |
| *Primary metadata is invalid; the backup copy was used* | The image is damaged but recoverable; the rebuilt super gets fresh, valid metadata. |
| *is a xz/gzip/zip archive* | Extract the GSI first. |
| *no ext4 toolchain* | Shrinking is unavailable; install WSL + e2fsprogs or provide e2fsprogs binaries. |
| *e2fsck reported uncorrected errors* | The working copy is corrupt; the original is untouched. |
