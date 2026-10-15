#!/usr/bin/env python3
"""Generate deterministic ICO fixtures and Pillow references; no downloads."""
import hashlib
import io
import json
import struct
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parent
DIRECTORY = ROOT / 'corpus' / 'ico'


def wrap(entries):
    offset = 6 + 16 * len(entries)
    directory = bytearray(struct.pack('<HHH', 0, 1, len(entries)))
    payload = bytearray()
    for w, h, bits, data in entries:
        directory += struct.pack('<BBBBHHII', w % 256, h % 256, 0, 0, 1, bits, len(data), offset)
        payload += data
        offset += len(data)
    return bytes(directory + payload)


def dib(bits, width=17, height=5):
    palette = b''
    if bits <= 8:
        palette = b''.join(bytes(((i * 53) % 256, (i * 97) % 256, (i * 193) % 256, 0)) for i in range(1 << bits))
    stride = ((width * bits + 31) // 32) * 4
    mask_stride = ((width + 31) // 32) * 4
    raster, masks = bytearray(), bytearray()
    for y in reversed(range(height)):
        row, mask = bytearray(stride), bytearray(mask_stride)
        for x in range(width):
            if bits <= 8:
                index = (x + y * 3) % (1 << bits)
                bit = x * bits
                row[bit // 8] |= index << (8 - bits - bit % 8)
            else:
                p = x * (bits // 8)
                row[p:p + 3] = bytes(((x * 11) % 256, (y * 47) % 256, (x * 23 + y) % 256))
                if bits == 32:
                    row[p + 3] = (0, 71, 255)[(x + y) % 3]
            if (x + y) % 3 == 0:
                mask[x // 8] |= 0x80 >> (x % 8)
        raster += row
        masks += mask
    header = struct.pack('<IiiHHIIiiII', 40, width, height * 2, 1, bits, 0, len(raster) + len(masks), 0, 0, 0, 0)
    return (width, height, bits, header + palette + raster + masks)


def generate():
    DIRECTORY.mkdir(parents=True, exist_ok=True)
    entries = []
    for bits in (1, 4, 8, 24, 32):
        entry = dib(bits)
        (DIRECTORY / f'dib{bits}.ico').write_bytes(wrap([entry]))
        entries.append(entry)
    for size in (16, 32, 256):
        image = Image.new('RGBA', (size, size))
        image.putdata([((x * 13) % 256, (y * 17) % 256, (x + y) % 256, (0, 128, 255)[(x + y) % 3])
                       for y in range(size) for x in range(size)])
        output = io.BytesIO()
        image.save(output, 'PNG')
        entry = (size, size, 32, output.getvalue())
        (DIRECTORY / f'png{size}.ico').write_bytes(wrap([entry]))
        entries.append(entry)
    (DIRECTORY / 'mixed.ico').write_bytes(wrap(entries))
    image.save(DIRECTORY / 'pillow.ico', sizes=[(16, 16), (32, 32), (64, 64), (256, 256)])
    for mode in ('1', 'L', 'P', 'RGB'):
        image.convert(mode).save(DIRECTORY / f'pillow_{mode}.ico', sizes=[(16, 16)])


def references():
    refs = {}
    for path in sorted(DIRECTORY.glob('*.ico')):
        data = path.read_bytes()
        count = struct.unpack_from('<H', data, 4)[0]
        variants = []
        for i in range(count):
            w, h, _, _, _, bits, length, offset = struct.unpack_from('<BBBBHHII', data, 6 + i * 16)
            w, h = w or 256, h or 256
            # Isolate each directory entry so duplicate dimensions remain independently testable.
            single = wrap([(w, h, bits, data[offset:offset + length])])
            payload = data[offset:offset + length]
            # Pillow's ICO wrapper loses palette PNG tRNS. Decode the embedded PNG directly;
            # this agrees with ImageMagick's ICO decoder and retains partial alpha.
            source = payload if payload.startswith(b'\x89PNG\r\n\x1a\n') else single
            with Image.open(io.BytesIO(source)) as im:
                pixels = im.convert('RGBA').tobytes()
            variants.append({'width': w, 'height': h, 'sha256': hashlib.sha256(pixels).hexdigest()})
        refs[path.name] = variants
    (ROOT / 'ico_reference.json').write_text(json.dumps(refs, indent=2) + '\n')


if __name__ == '__main__':
    generate()
    references()
