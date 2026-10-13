#!/usr/bin/env python3
"""Generate JPEG encoding hashes using Pillow 10.2 / libjpeg-turbo 2.1.5.
Run with python3 -I; all inputs are committed corpus files. No downloads.
"""
import hashlib
import io
import json
from pathlib import Path
from PIL import Image, features

ROOT = Path(__file__).resolve().parent

def main():
    if features.version('libjpeg_turbo') != '2.1.5':
        raise RuntimeError('Encoding references require libjpeg-turbo 2.1.5')
    entries = []
    sources = ['src/photo.png', 'src/photo_odd.png', 'src/graphic.png',
               'src/tiny.png', 'src/thin.png', 'src/wide.png', 'src/odd_alpha.png',
               'src/photo_alpha.png', 'png/gray8.png', 'png/graya8.png']
    for source in sources:
        rgba = Image.open(ROOT / 'corpus' / source).convert('RGBA')
        rgb = Image.alpha_composite(Image.new('RGBA', rgba.size, (255,255,255,255)), rgba).convert('RGB')
        if all(r == g == b for r,g,b in rgb.getdata()):
            rgb = rgb.convert('L')
        for quality in [1, 25, 50, 75, 90, 100]:
            for sampling in ([2] if rgb.mode == 'L' else [0,1,2]):
                for optimize, progressive in [(False,False), (True,False), (False,True)]:
                    output = io.BytesIO()
                    rgb.save(output, 'JPEG', quality=quality, subsampling=sampling,
                             optimize=optimize, progressive=progressive, dpi=(96,96))
                    data = output.getvalue()
                    decoded = Image.open(io.BytesIO(data)).convert('RGBA')
                    entries.append(dict(Source=source, Quality=quality, Subsampling=sampling,
                                        Optimize=optimize, Progressive=progressive,
                                        Sha256=hashlib.sha256(data).hexdigest(), Size=len(data),
                                        RgbaSha256=hashlib.sha256(decoded.tobytes()).hexdigest()))
    (ROOT / 'jpeg_encoder_reference.json').write_text(json.dumps(entries, indent=2)+'\n')
    print(f'{len(entries)} JPEG encoding references written')

if __name__ == '__main__':
    main()
