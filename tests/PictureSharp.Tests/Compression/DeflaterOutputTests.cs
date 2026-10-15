using System.Security.Cryptography;
using PictureSharp.Compression;

namespace PictureSharp.Tests.Compression;

/// <summary>Pins the deflate stream so match-finder refactors cannot change the compressed bytes.</summary>
public class DeflaterOutputTests
{
    private static byte[] Input(string name)
    {
        var rng = new Random(2024);
        var data = new byte[60_000];
        switch (name)
        {
            case "text":
                for (int i = 0; i < data.Length; i++)
                {
                    data[i] = (byte)"the quick brown fox jumps over the lazy dog "[(i * 7 + rng.Next(3)) % 44];
                }

                break;
            case "runs":
                for (int i = 0; i < data.Length; i += rng.Next(1, 600))
                {
                    data.AsSpan(i, Math.Min(rng.Next(1, 600), data.Length - i)).Fill((byte)rng.Next(4));
                }

                break;
            default: // repeated blocks with small mutations: long matches near the 258-byte limit
                rng.NextBytes(data.AsSpan(0, 1500));
                for (int i = 1500; i < data.Length; i++)
                {
                    data[i] = rng.Next(40) == 0 ? (byte)rng.Next(256) : data[i - 1500];
                }

                break;
        }

        return data;
    }

    public static TheoryData<string, int, string> Golden => new()
    {
        { "text", 1, "3FE80C5A5DF7ABA4F3C35D0D69D09E0D309AD526BD34BC75846EFE0934F26699" }, { "text", 6, "AAEE7F8EE26976A6E50DE8148AA7CCC56D31E2AAC9B79DBB706FE561A46B10C4" }, { "text", 9, "FFBF41CF2CABF0F564C723A87B94FC79A92333C589423B6424718F099F2B2E63" },
        { "runs", 1, "9B82E3502AE150BC021E61C22992CD9E3BE4E27EF29BE84C19C312AF043A1EBB" }, { "runs", 6, "5084ACAFEF53A677C0F8ACFC6FEFE7A93DEB71CEA1407B7B29F84C84DD8B87EF" }, { "runs", 9, "5EC98692F5AC6900E39329AC31E2417D4DA9AD881044E9C0BCA694B596639B6E" },
        { "blocks", 1, "B70525E1D52510CCE50BD85452C0F9DBE0675E82D42D6938419ACFAD6C1EDE34" }, { "blocks", 6, "B70525E1D52510CCE50BD85452C0F9DBE0675E82D42D6938419ACFAD6C1EDE34" }, { "blocks", 9, "B70525E1D52510CCE50BD85452C0F9DBE0675E82D42D6938419ACFAD6C1EDE34" },
    };

    [Theory]
    [MemberData(nameof(Golden))]
    public void Compress_ProducesPinnedBytes(string input, int level, string sha256)
    {
        byte[] compressed = Deflater.Compress(Input(input), level);
        Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(compressed)));
    }
}
