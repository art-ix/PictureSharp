"""Generate tiny synthetic EXIF fixtures and independently read them with Pillow.

Run with python3 -I tests/assets/metadata_reference.py. No external corpus is used.
"""
import json
from pathlib import Path
from PIL import Image, TiffImagePlugin

ROOT = Path(__file__).resolve().parent
DEST = ROOT / "corpus" / "metadata"
DEST.mkdir(exist_ok=True)
result = {}
for endian, name in (("<", "little"), (">", "big")):
    exif = Image.Exif()
    exif.endian = endian
    exif[271] = "PictureSharp Synthetic"
    exif[272] = "Test Camera"
    exif[274] = 6
    exif[282] = TiffImagePlugin.IFDRational(300, 1)
    exif[283] = TiffImagePlugin.IFDRational(240, 1)
    exif[296] = 2
    exif[306] = "2026:10:15 12:34:56"
    exif[0xc001] = b"opaque-value-preserve-offset"
    exif[34665] = {
        33434: TiffImagePlugin.IFDRational(1, 125),
        33437: TiffImagePlugin.IFDRational(28, 10),
        34855: 400,
        36867: "2026:10:14 10:20:30",
        36868: "2026:10:14 10:21:30",
        37386: TiffImagePlugin.IFDRational(50, 1),
        42032: "Synthetic Owner",
        0xc002: b"opaque-maker-note"
    }
    exif[34853] = {
        1: "N", 2: (TiffImagePlugin.IFDRational(52), TiffImagePlugin.IFDRational(14), TiffImagePlugin.IFDRational(30)),
        3: "E", 4: (TiffImagePlugin.IFDRational(21), TiffImagePlugin.IFDRational(0), TiffImagePlugin.IFDRational(45)),
        5: b"\x00", 6: TiffImagePlugin.IFDRational(123, 2),
        7: (TiffImagePlugin.IFDRational(10), TiffImagePlugin.IFDRational(20), TiffImagePlugin.IFDRational(30)),
        27: b"ASCII\x00\x00\x00secret-gps-processing", 29: "2026:10:14"
    }
    raw = exif.tobytes()[6:]
    (DEST / f"{name}.exif").write_bytes(raw)
    parsed = Image.Exif()
    parsed.load(b"Exif\x00\x00" + raw)
    camera = parsed.get_ifd(34665)
    gps = parsed.get_ifd(34853)
    result[name] = {"make": parsed[271], "model": parsed[272], "orientation": parsed[274],
                    "dpi": [float(parsed[282]), float(parsed[283])], "date": parsed[306],
                    "captured": camera[36867], "iso": camera[34855], "exposure": float(camera[33434]),
                    "aperture": float(camera[33437]), "focalLength": float(camera[37386]),
                    "latitude": [float(x) for x in gps[2]], "longitude": [float(x) for x in gps[4]],
                    "altitude": float(gps[6])}
(ROOT / "metadata_reference.json").write_text(json.dumps(result, indent=2) + "\n")
