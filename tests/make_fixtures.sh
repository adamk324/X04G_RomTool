#!/bin/sh
# Regenerates the small ext4 fixtures used by the unit tests (needs e2fsprogs >= 1.43 for mke2fs -d).
# The fixtures are synthetic: they only contain build.prop files written below, not any Xiaomi or GSI content.
set -e
OUT="$(cd "$(dirname "$0")" && pwd)/X04G.Core.Tests/Fixtures"
T=$(mktemp -d)
export E2FSPROGS_FAKE_TIME=1700000000
mk() { # name size_kib blocksize type extra_opts rootdir
  rm -f "$T/$1"; truncate -s "${2}K" "$T/$1"
  mke2fs -q -F -t "$4" -b "$3" -U 11111111-2222-3333-4444-555555555555 -E hash_seed=11111111-2222-3333-4444-555555555555 $5 -d "$6" "$T/$1"
  gzip -9 -n -c "$T/$1" > "$OUT/$1.gz"
}
# Stock-like system (system-as-root), vendor, product
R=$T/sys; mkdir -p $R/system/bin $R/system/apex/com.android.vndk.current $R/system/lib/vndk-sp-29
printf 'ro.build.version.sdk=29\nro.build.version.release=10\nro.product.cpu.abilist=armeabi-v7a,armeabi\nro.product.cpu.abilist32=armeabi-v7a,armeabi\nro.build.display.id=TEST-STOCK\n' > $R/system/build.prop
echo init > $R/system/bin/init; ln -s /system/bin/init $R/init
head -c 300000 /dev/zero | tr '\0' 'S' > $R/system/blob.bin
mk system_stock.img 4096 4096 ext4 "" $R
R=$T/ven; mkdir -p $R/etc
printf 'ro.vndk.version=29\nro.vendor.build.version.sdk=29\nro.vendor.build.version.release=10\nro.treble.enabled=true\nro.product.vendor.cpu.abilist=armeabi-v7a,armeabi\nro.product.vendor.device=test_clock\n' > $R/build.prop
mk vendor_stock.img 2048 4096 ext4 "" $R
R=$T/prod; mkdir -p $R/app; printf 'ro.product.product.name=test\n' > $R/build.prop
mk product_stock.img 1024 4096 ext4 "" $R
# GSI-like: SAR, SDK 30, arm64, ships VNDK v29 apex
R=$T/gsi; mkdir -p $R/system/bin $R/system/system_ext/apex
printf 'ro.system.build.version.sdk=30\nro.system.build.version.release=11\nro.system.product.cpu.abilist=arm64-v8a,armeabi-v7a,armeabi\nro.system.product.cpu.abilist64=arm64-v8a\nro.build.display.id=TEST-GSI\n' > $R/system/build.prop
echo gsi > $R/system/system_ext/apex/com.android.vndk.v29.apex
echo init > $R/system/bin/init; ln -s /system/bin/init $R/init
mk gsi_sar_arm64_sdk30.img 6144 4096 ext4 "" $R
# Old, non-SAR GSI (SDK 28) for negative tests
R=$T/old; mkdir -p $R/bin
printf 'ro.build.version.sdk=28\nro.build.version.release=9\nro.product.cpu.abilist=armeabi-v7a,armeabi\n' > $R/build.prop
echo init > $R/bin/init
mk gsi_nonsar_sdk28.img 2048 4096 ext4 "" $R
# Reader edge cases: ext2 with 1 KiB blocks (indirect block maps) and ext4 inline_data
R=$T/e2; mkdir -p $R/d; seq 1 70000 > $R/d/big.bin; printf 'hello' > $R/small.txt
mk ext2_blockmap.img 2048 1024 ext2 "" $R
sha256sum $R/d/big.bin | cut -d' ' -f1 > "$OUT/ext2_blockmap.big.sha256"
R=$T/inl; mkdir -p $R/dir; printf 'inline!' > $R/dir/tiny.txt
mk ext4_inline.img 1024 4096 ext4 "-O inline_data" $R
rm -rf "$T"
# Vendor-like image without metadata_csum/journal (like the X04G stock vendor) for in-place patch tests
T=$(mktemp -d); R=$T/vp; mkdir -p $R/etc
python3 -c "
import sys
body=''.join('        <MediaCodec name=\"OMX.TEST.DEC.%02d\" type=\"audio/x-test-%02d\">\n            <Limit name=\"concurrent-instances\" max=\"16\" />\n        </MediaCodec>\n\n'%(i,i) for i in range(40))
open('$R/etc/codecs.xml','w').write('<?xml version=\"1.0\" encoding=\"utf-8\" ?>\n<Included>\n    <Decoders>\n'+body+'    </Decoders>\n</Included>\n')"
printf 'ro.vndk.version=29\n' > $R/build.prop
truncate -s 1024K $T/vendor_nocsum.img
mke2fs -q -F -t ext4 -b 4096 -O ^metadata_csum,^has_journal,^64bit -U 11111111-2222-3333-4444-555555555555 -E hash_seed=11111111-2222-3333-4444-555555555555 -d $R $T/vendor_nocsum.img
debugfs -w -R "ea_set /etc/codecs.xml security.selinux u:object_r:vendor_configs_file:s0" $T/vendor_nocsum.img >/dev/null 2>&1
gzip -9 -n -c $T/vendor_nocsum.img > "$OUT/vendor_nocsum.img.gz"
rm -rf "$T"
