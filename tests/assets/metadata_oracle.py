"""Validate metadata edits and every composited frame with Pillow, without rewriting inputs."""
import sys
from PIL import Image

with Image.open(sys.argv[1]) as before, Image.open(sys.argv[2]) as after:
    assert before.size == after.size
    assert getattr(before, "n_frames", 1) == getattr(after, "n_frames", 1)
    assert before.info.get("loop") == after.info.get("loop")
    for index in range(getattr(before, "n_frames", 1)):
        before.seek(index)
        after.seek(index)
        assert before.convert("RGBA").tobytes() == after.convert("RGBA").tobytes(), index
        assert before.info.get("duration") == after.info.get("duration"), index
    after.seek(0)
    exif = after.getexif()
    assert exif[272] == "Edited Camera"
    assert exif.get_ifd(34665)[36867] == "2026:10:14 10:20:30"
    assert exif.get_ifd(34665)[34855] == 400
    assert not exif.get_ifd(34853)
    assert after.info["icc_profile"] == bytes(range(256)) * 313
print("Pillow metadata and all RGBA frames match")
