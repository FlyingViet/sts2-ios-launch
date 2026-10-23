#!/usr/bin/env python3
"""Retags arm64 iOS device code as iOS Simulator code, in place: a static library (.a) or a single Mach-O file.

Apple silicon Macs run arm64 iOS code natively in the Simulator, but the linker and dyld refuse code marked for
devices. Godot's official iOS template only has an x86_64 Simulator library, so build.sh --simulator uses this to
link its arm64 device library instead. Only the platform field of each object's LC_BUILD_VERSION changes (IOS ->
IOSSIMULATOR), so sizes and the archive's symbol table stay valid.
"""
import struct
import sys

LC_BUILD_VERSION = 0x32
LC_VERSION_MIN_IPHONEOS = 0x25
PLATFORM_IOS, PLATFORM_IOSSIMULATOR = 2, 7
MH_MAGIC_64 = 0xFEEDFACF


def retag(buf, off):
    magic, _cpu, _sub, _type, ncmds, _size, _flags, _res = struct.unpack_from("<IiiIIIII", buf, off)
    if magic != MH_MAGIC_64:
        return "not-macho"
    p = off + 32
    result = "no-build-version"
    for _ in range(ncmds):
        cmd, size = struct.unpack_from("<II", buf, p)
        if cmd == LC_BUILD_VERSION:
            platform = struct.unpack_from("<I", buf, p + 8)[0]
            if platform == PLATFORM_IOS:
                struct.pack_into("<I", buf, p + 8, PLATFORM_IOSSIMULATOR)
                result = "retagged"
            elif platform == PLATFORM_IOSSIMULATOR:
                result = "already"
            else:
                result = "platform-%d" % platform
        elif cmd == LC_VERSION_MIN_IPHONEOS:
            result = "version-min"  # older format, can't be changed in place
        p += size
    return result


def main(path):
    buf = bytearray(open(path, "rb").read())
    counts = {}
    if buf[:8] == b"!<arch>\n":
        p = 8
        while p + 60 <= len(buf):
            name = buf[p:p + 16].decode().strip()
            size = int(buf[p + 48:p + 58].decode().strip())
            data = p + 60
            name_len = 0
            if name.startswith("#1/"):
                name_len = int(name[3:])
                name = buf[data:data + name_len].rstrip(b"\0").decode()
            r = "symbol-table" if name.startswith("__.SYMDEF") else retag(buf, data + name_len)
            counts[r] = counts.get(r, 0) + 1
            p = data + size + (size & 1)
    else:
        r = retag(buf, 0)
        counts[r] = 1
    bad = {k: v for k, v in counts.items() if k not in ("retagged", "already", "symbol-table")}
    if bad:
        sys.exit("%s: can't retag %s" % (path, bad))
    open(path, "wb").write(buf)
    print("%s: %s" % (path, counts))


if __name__ == "__main__":
    for arg in sys.argv[1:]:
        main(arg)
