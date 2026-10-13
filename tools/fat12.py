#!/usr/bin/env python3
"""Extract files from a raw FAT12 floppy image."""
import struct, sys, os

def u16(b, o): return struct.unpack_from('<H', b, o)[0]

def extract(img_path, out_dir):
    data = open(img_path, 'rb').read()
    bps        = u16(data, 11)          # bytes per sector
    spc        = data[13]               # sectors per cluster
    reserved   = u16(data, 14)
    nfats      = data[16]
    root_ents  = u16(data, 17)
    spf        = u16(data, 22)          # sectors per FAT
    fat_off    = reserved * bps
    root_off   = fat_off + nfats * spf * bps
    root_bytes = root_ents * 32
    data_off   = root_off + root_bytes

    # unpack 12-bit FAT entries
    fat = data[fat_off:fat_off + spf * bps]
    def fat_entry(n):
        i = n + n // 2
        v = fat[i] | (fat[i + 1] << 8)
        return (v >> 4) if (n & 1) else (v & 0x0FFF)

    os.makedirs(out_dir, exist_ok=True)
    print(f"{img_path}: bps={bps} spc={spc} fats={nfats} spf={spf} root={root_ents}")

    def walk(dir_bytes, prefix=''):
        for o in range(0, len(dir_bytes), 32):
            e = dir_bytes[o:o + 32]
            if not e or e[0] in (0x00,):
                break
            if e[0] == 0xE5 or e[11] & 0x08:      # deleted or volume label
                continue
            if e[11] == 0x0F:                      # LFN entry
                continue
            name = e[0:8].decode('cp437').rstrip()
            ext  = e[8:11].decode('cp437').rstrip()
            fn   = f"{name}.{ext}" if ext else name
            if fn in ('.', '..'):
                continue
            start = u16(e, 26)
            size  = struct.unpack_from('<I', e, 28)[0]
            isdir = bool(e[11] & 0x10)
            chunks, c = [], start
            while 2 <= c < 0xFF0:
                off = data_off + (c - 2) * spc * bps
                chunks.append(data[off:off + spc * bps])
                c = fat_entry(c)
            blob = b''.join(chunks)
            if isdir:
                print(f"  DIR  {prefix}{fn}/")
                walk(blob, prefix + fn + '/')
            else:
                out = os.path.join(out_dir, prefix.replace('/', os.sep), fn)
                os.makedirs(os.path.dirname(out), exist_ok=True)
                open(out, 'wb').write(blob[:size])
                print(f"  {size:8d}  {prefix}{fn}")

    walk(data[root_off:root_off + root_bytes])

if __name__ == '__main__':
    from usage import argv
    extract(*argv(2, 'fat12.py <disk.img> <output-dir>'))
