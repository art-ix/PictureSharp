using System.Buffers.Binary;
using System.Text.Json;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Metadata;

public class ExifProfileTests
{
    [Test]
    [Arguments("little")]
    [Arguments("big")]
    public async Task TypedValues_MatchPillowReferences(string name)
    {
        var metadata = new ImageMetadata { Exif = Corpus.Read($"metadata/{name}.exif") };
        using JsonDocument references = JsonDocument.Parse(File.ReadAllText(Path.Combine(Corpus.Assets, "metadata_reference.json")));
        JsonElement expected = references.RootElement.GetProperty(name);
        ExifProfile profile = metadata.ExifProfile;
        await Assert.That(profile.TryGetValue(ExifTag.Make, out var make)).IsTrue();
        await Assert.That(make).IsEqualTo(expected.GetProperty("make").GetString());
        await Assert.That(profile.TryGetValue(ExifTag.Model, out var model)).IsTrue();
        await Assert.That(model).IsEqualTo(expected.GetProperty("model").GetString());
        await Assert.That(profile.TryGetValue(ExifTag.Orientation, out var orientation)).IsTrue();
        await Assert.That((int)orientation).IsEqualTo(expected.GetProperty("orientation").GetInt32());
        await Assert.That(profile.TryGetValue(ExifTag.DateTimeOriginal, out var date)).IsTrue();
        await Assert.That(date).IsEqualTo(new DateTime(2026, 10, 14, 10, 20, 30));
        await Assert.That(date.Kind).IsEqualTo(DateTimeKind.Unspecified);
        await Assert.That(profile.TryGetValue(ExifTag.DateTime, out var modified)).IsTrue();
        await Assert.That(modified).IsEqualTo(new DateTime(2026, 10, 15, 12, 34, 56));
        await Assert.That(profile.TryGetValue(ExifTag.DateTimeDigitized, out var digitized)).IsTrue();
        await Assert.That(digitized).IsEqualTo(new DateTime(2026, 10, 14, 10, 21, 30));
        await Assert.That(profile.TryGetValue(ExifTag.IsoSpeed, out var iso)).IsTrue();
        await Assert.That((int)iso).IsEqualTo(expected.GetProperty("iso").GetInt32());
        await Assert.That(profile.TryGetValue(ExifTag.ExposureTime, out var exposure)).IsTrue();
        await Assert.That(exposure.ToDouble()).IsEqualTo(expected.GetProperty("exposure").GetDouble());
        await Assert.That(profile.TryGetValue(ExifTag.FNumber, out var aperture)).IsTrue();
        await Assert.That(aperture.ToDouble()).IsEqualTo(expected.GetProperty("aperture").GetDouble());
        await Assert.That(profile.TryGetValue(ExifTag.FocalLength, out var focal)).IsTrue();
        await Assert.That(focal.ToDouble()).IsEqualTo(expected.GetProperty("focalLength").GetDouble());
        await Assert.That(profile.TryGetValue(ExifTag.XResolution, out var x)).IsTrue();
        await Assert.That(x.ToDouble()).IsEqualTo(expected.GetProperty("dpi")[0].GetDouble());
        await Assert.That(profile.TryGetValue(ExifTag.YResolution, out var y)).IsTrue();
        await Assert.That(y.ToDouble()).IsEqualTo(expected.GetProperty("dpi")[1].GetDouble());
        await Assert.That(profile.TryGetValue(ExifTag.GPSLatitude, out var latitude)).IsTrue();
        await Assert.That(latitude!.Select(v => v.ToDouble())).IsEquivalentTo(expected.GetProperty("latitude").EnumerateArray().Select(v => v.GetDouble()), CollectionOrdering.Matching);
        await Assert.That(profile.TryGetValue(ExifTag.GPSLongitude, out var longitude)).IsTrue();
        await Assert.That(longitude!.Select(v => v.ToDouble())).IsEquivalentTo(expected.GetProperty("longitude").EnumerateArray().Select(v => v.GetDouble()), CollectionOrdering.Matching);
        await Assert.That(profile.TryGetValue(ExifTag.GPSAltitude, out var altitude)).IsTrue();
        await Assert.That(altitude.ToDouble()).IsEqualTo(expected.GetProperty("altitude").GetDouble());
    }

    [Test]
    [Arguments("little")]
    [Arguments("big")]
    public async Task Edits_PreserveUnknownOffsets_AndPhysicallyRemoveGps(string name)
    {
        byte[] original = Corpus.Read($"metadata/{name}.exif");
        var metadata = new ImageMetadata { Exif = original, Xmp = "private-xmp"u8.ToArray() };
        var profile = metadata.ExifProfile;
        int opaque = original.AsSpan().IndexOf("opaque-value-preserve-offset"u8);
        profile.SetValue(ExifTag.Model, "New Model");
        await Assert.That(metadata.Exif!.AsSpan().IndexOf("opaque-value-preserve-offset"u8)).IsEqualTo(opaque);
        await Assert.That(profile.TryGetValue(ExifTag.Model, out var model)).IsTrue();
        await Assert.That(model).IsEqualTo("New Model");
        profile.RemoveGps();
        await Assert.That(profile.TryGetValue(ExifTag.GPSLatitude, out _)).IsFalse();
        await Assert.That(metadata.Exif!.AsSpan().IndexOf("secret-gps-processing"u8)).IsEqualTo(-1);
        await Assert.That(metadata.Exif!.AsSpan().IndexOf("opaque-value-preserve-offset"u8)).IsEqualTo(opaque);
        await Assert.That(metadata.Exif!.AsSpan().IndexOf("opaque-maker-note"u8) >= 0).IsTrue();
        await Sequence.AssertEqual("private-xmp"u8.ToArray(), metadata.Xmp);
        profile.KeepOnly(ExifTag.Model, ExifTag.ExposureTime);
        await Assert.That(profile.TryGetValue(ExifTag.Model, out _)).IsTrue();
        await Assert.That(profile.TryGetValue(ExifTag.ExposureTime, out _)).IsTrue();
        await Assert.That(profile.TryGetValue(ExifTag.Make, out _)).IsFalse();
        await Assert.That(metadata.Exif!.AsSpan().IndexOf("opaque"u8)).IsEqualTo(-1);
        await Assert.That(profile.RemoveValue(ExifTag.Model)).IsTrue();
        await Assert.That(profile.RemoveValue(ExifTag.Model)).IsFalse();
        profile.KeepOnly();
        await Assert.That(metadata.Exif).IsNull();
    }

    [Test]
    public async Task LiveView_RawReplacementClonesAndClear_ShareOneState()
    {
        var metadata = new ImageMetadata();
        var profile = metadata.ExifProfile;
        await Assert.That(profile.TryGetValue(ExifTag.Model, out _)).IsFalse();
        await Assert.That(metadata.IsEmpty).IsTrue();
        profile.SetValue(ExifTag.Model, "Camera");
        profile.SetValue(ExifTag.Orientation, ExifOrientation.RightTop);
        await Assert.That(metadata.Orientation).IsEqualTo(ExifOrientation.RightTop);
        metadata.Orientation = ExifOrientation.TopLeft;
        await Assert.That(profile.TryGetValue(ExifTag.Orientation, out var orientation)).IsTrue();
        await Assert.That(orientation).IsEqualTo(ExifOrientation.TopLeft);
        metadata.Exif = Corpus.Read("metadata/big.exif");
        profile.SetValue(ExifTag.GPSLatitude, [new(1, 1), new(2, 1), new(3, 1)]);
        profile.TryGetValue(ExifTag.GPSLatitude, out var latitude);
        latitude![0] = new(99, 1);
        profile.TryGetValue(ExifTag.GPSLatitude, out var reread);
        await Assert.That(reread![0]).IsEqualTo(new ExifRational(1, 1));
        ImageMetadata clone = metadata.Clone();
        clone.ExifProfile.SetValue(ExifTag.Model, "Clone");
        profile.TryGetValue(ExifTag.Model, out var model);
        await Assert.That(model).IsEqualTo("Test Camera");
        metadata.Clear();
        await Assert.That(profile.TryGetValue(ExifTag.Model, out _)).IsFalse();
        await Assert.That(metadata.IsEmpty).IsTrue();
    }

    [Test]
    [Arguments("little")]
    [Arguments("big")]
    public async Task MalformedDirectories_RejectEditsWithoutChangingRawBlock(string name)
    {
        byte[] valid = Corpus.Read($"metadata/{name}.exif");
        bool big = name == "big";
        uint Read32(ReadOnlySpan<byte> s) => big ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
        ushort Read16(ReadOnlySpan<byte> s) => big ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);
        void Write32(Span<byte> s, uint v) { if (big) BinaryPrimitives.WriteUInt32BigEndian(s, v); else BinaryPrimitives.WriteUInt32LittleEndian(s, v); }
        int root = (int)Read32(valid.AsSpan(4));
        int count = Read16(valid.AsSpan(root));
        foreach (string mutation in new[] { "root-offset", "cycle", "value-offset", "truncated" })
        {
            byte[] corrupt = (byte[])valid.Clone();
            switch (mutation)
            {
                case "root-offset": Write32(corrupt.AsSpan(4), uint.MaxValue); break;
                case "cycle": Write32(corrupt.AsSpan(root + 2 + (count * 12)), (uint)root); break;
                case "value-offset": Write32(corrupt.AsSpan(root + 10), uint.MaxValue); break;
                default: corrupt = corrupt[..9]; break;
            }
            var metadata = new ImageMetadata { Exif = corrupt };
            await Assert.ThrowsExactlyAsync<ImageFormatException>(() => Task.Run(() => metadata.ExifProfile.SetValue(ExifTag.Model, "X")));
            await Sequence.AssertEqual(corrupt, metadata.Exif);
        }
    }

    [Test]
    public async Task MutatedTiffInputs_NeverExposeUncheckedExceptions()
    {
        byte[] valid = Corpus.Read("metadata/little.exif");
        var random = new Random(6101);
        for (int i = 0; i < 128; i++)
        {
            byte[] bytes = (byte[])valid.Clone();
            int offset = random.Next(bytes.Length);
            bytes[offset] ^= (byte)random.Next(1, 256);
            var metadata = new ImageMetadata { Exif = bytes };
            try
            {
                _ = metadata.ExifProfile.TryGetValue(ExifTag.Model, out _);
                metadata.ExifProfile.RemoveGps();
                metadata.ExifProfile.SetValue(ExifTag.Model, "Fuzz");
            }
            catch (Exception ex)
            {
                await Assert.That(ex is ImageFormatException or NotSupportedException).IsTrue().Because(ex.ToString());
            }
        }
    }

    [Test]
    public async Task RemovingSharedGpsStorage_FailsWithoutDamagingRetainedValues()
    {
        byte[] bytes = new byte[80];
        "II*\0"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), 2);
        void Entry(int offset, ushort tag, ushort type, uint count, uint value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), tag);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 2), type);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 4), count);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 8), value);
        }
        Entry(10, 0x0110, 2, 24, 38);
        Entry(22, 0x8825, 4, 1, 62);
        for (int i = 0; i < 3; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(38 + (8 * i)), (uint)(i + 1));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(42 + (8 * i)), 1);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(62), 1);
        Entry(64, 2, 5, 3, 38);
        var metadata = new ImageMetadata { Exif = bytes };
        await Assert.That(metadata.ExifProfile.TryGetValue(ExifTag.GPSLatitude, out _)).IsTrue();
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => Task.Run(() => metadata.ExifProfile.RemoveGps()));
        await Sequence.AssertEqual(bytes, metadata.Exif);
    }

    [Test]
    public async Task LargeOpaqueBlocks_RemainOwnedAndCanBeDiscardedExplicitly()
    {
        byte[] original = [.. Corpus.Read("metadata/little.exif"), .. new byte[2 * 1024 * 1024]];
        var metadata = new ImageMetadata { Exif = original };
        metadata.ExifProfile.SetValue(ExifTag.Model, "Large");
        await Assert.That(metadata.GetBlockLength(MetadataKinds.Exif) > 2 * 1024 * 1024).IsTrue();
        metadata.ExifProfile.KeepOnly(ExifTag.Model);
        await Assert.That(metadata.GetBlockLength(MetadataKinds.Exif) < 100).IsTrue();
    }

    [Test]
    public async Task InvalidValues_AndAbsentValues_HaveDefinedBehavior()
    {
        var metadata = new ImageMetadata();
        metadata.ExifProfile.SetValue(ExifTag.Model, "Camera");
        byte[] before = metadata.Exif!;
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Task.Run(() => metadata.ExifProfile.SetValue(ExifTag.Model, "a\0b")));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Task.Run(() => metadata.ExifProfile.SetValue(ExifTag.DateTimeOriginal, DateTime.UtcNow)));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => Task.Run(() => metadata.ExifProfile.SetValue(ExifTag.Orientation, ExifOrientation.Unknown)));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Task.Run(() => metadata.ExifProfile.SetValue(ExifTag.FNumber, default(ExifRational))));
        await Sequence.AssertEqual(before, metadata.Exif);
        metadata.ExifProfile.SetValue(ExifTag.DateTimeOriginal, new DateTime(2026, 1, 2, 3, 4, 5));
        await Assert.That(metadata.ExifProfile.TryGetValue(ExifTag.DateTimeOriginal, out _)).IsTrue();
    }
}
