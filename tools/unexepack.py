#!/usr/bin/env python3
"""Decompress an EXEPACK-packed 16-bit DOS executable.

EXEPACK stores the load module back to front as a simple RLE stream, with a
small unpacker stub appended after it. The stub's header carries the real entry
point and the unpacked length.

Reading the stream backwards, each command is stored as:

    [fill byte]? [length lo] [length hi] [command]

so going from the end you meet the command first, then the length high byte,
then the low byte. Command 0xB0/0xB1 fills a run with one byte; 0xB2/0xB3
copies a literal run. Bit 0 set marks the last command.
"""
import struct, sys


class ExepackError(Exception):
    pass


def unpack(data: bytes):
    if data[:2] not in (b'MZ', b'ZM'):
        raise ExepackError('not an MZ executable')

    lastpage, pages, nreloc, hdrpara = struct.unpack_from('<HHHH', data, 2)
    _minal, _maxal, ss, sp, _csum, ip, cs = struct.unpack_from('<HHHHHHH', data, 10)

    hdr = hdrpara * 16
    exepack_off = hdr + cs * 16

    h = data[exepack_off:exepack_off + 18]
    if h[16:18] != b'RB':
        raise ExepackError(f'no RB signature at 0x{exepack_off:X} (got {h[16:18]!r})')

    real_ip, real_cs, _mem_start, exepack_size, real_sp, real_ss, dest_para, skip_para = \
        struct.unpack_from('<HHHHHHHH', h, 0)

    packed = bytearray(data[hdr:exepack_off])
    out = bytearray(dest_para * 16)

    # Trim the 0xFF padding the packer leaves at the end of the stream.
    p = len(packed) - 1
    while p >= 0 and packed[p] == 0xFF:
        p -= 1

    d = len(out)

    while True:
        if p < 3:
            raise ExepackError('ran off the start of the packed stream')

        cmd = packed[p]; p -= 1
        hi = packed[p]; p -= 1
        lo = packed[p]; p -= 1
        length = lo | (hi << 8)

        op = cmd & 0xFE
        if op == 0xB0:
            fill = packed[p]; p -= 1
            if d - length < 0:
                raise ExepackError('fill run overflows the output buffer')
            d -= length
            out[d:d + length] = bytes([fill]) * length

        elif op == 0xB2:
            if d - length < 0 or p - length + 1 < 0:
                raise ExepackError('copy run overflows a buffer')
            d -= length
            p -= length
            out[d:d + length] = packed[p + 1:p + 1 + length]

        else:
            raise ExepackError(f'unknown command 0x{cmd:02X} at offset {p + 3}')

        if cmd & 1:
            break

    skip = (skip_para - 1) * 16
    body = bytes(out[skip:]) if skip else bytes(out)

    return {
        'body': body,
        'real_cs': real_cs, 'real_ip': real_ip,
        'real_ss': real_ss, 'real_sp': real_sp,
        'dest_len': dest_para * 16,
        'stub_size': exepack_size,
        'consumed_to': p,
    }


def main():
    from usage import argv
    src, dst = argv(2, 'unexepack.py <packed.exe> <unpacked.bin>')
    r = unpack(open(src, 'rb').read())

    open(dst, 'wb').write(r['body'])
    print(f"{src} -> {dst}")
    print(f"  unpacked {len(r['body'])} bytes (dest_len {r['dest_len']})")
    print(f"  real entry {r['real_cs']:04X}:{r['real_ip']:04X}  "
          f"stack {r['real_ss']:04X}:{r['real_sp']:04X}")
    print(f"  stream consumed down to offset {r['consumed_to']}")


if __name__ == '__main__':
    main()
