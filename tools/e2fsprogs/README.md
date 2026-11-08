# Optional: e2fsprogs for Windows

Place `e2fsck.exe` and `resize2fs.exe` (plus any DLLs they need, e.g. a Cygwin or MSYS2 build of e2fsprogs)
in this folder to enable the optional **Shrink ext4 images** step without WSL. They are copied next to
`X04GToolkit.exe` (into `tools\e2fsprogs`) when the application is built.

These binaries are **not** included in this repository (GPL-licensed third-party software). Alternatively,
install WSL (`wsl --install`) and run `sudo apt install e2fsprogs` inside the default distribution; the toolkit
detects it automatically.

Shrinking is the only operation that relies on external tools. Everything else (super parsing, unpacking,
repacking, sparse conversion, ext4 inspection, boot cmdline patching) is implemented natively in C#.
