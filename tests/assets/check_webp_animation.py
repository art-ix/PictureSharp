"""Decode each composed WebP canvas with Pillow for encoder oracle tests."""
from pathlib import Path
import sys
import json
from PIL import Image

with Image.open(sys.argv[1]) as image:
    directory = Path(sys.argv[2])
    durations = []
    for index in range(image.n_frames):
        image.seek(index)
        frame = image.convert('RGBA')
        durations.append(image.info['duration'])
        (directory / f'frame-{index}.rgba').write_bytes(frame.tobytes())
    print(json.dumps({'loop': image.info.get('loop', 0), 'background': image.info['background'], 'durations': durations}))
