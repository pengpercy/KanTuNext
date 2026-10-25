"""Packs PNGs into a Windows .ico (PNG-compressed entries, Vista+ and Win7 compatible).
usage: python3 tools/make_ico.py out.ico 16.png 32.png ... (each file named by its edge size)"""
import os, struct, sys
out, files = sys.argv[1], sys.argv[2:]
images = [(int(os.path.basename(f).split('.')[0]), open(f, 'rb').read()) for f in files]
header = struct.pack('<HHH', 0, 1, len(images))
offset = 6 + 16 * len(images)
entries, blobs = b'', b''
for size, data in images:
    edge = 0 if size >= 256 else size
    entries += struct.pack('<BBBBHHII', edge, edge, 0, 0, 1, 32, len(data), offset + len(blobs))
    blobs += data
open(out, 'wb').write(header + entries + blobs)
print(out, len(images), 'images')
