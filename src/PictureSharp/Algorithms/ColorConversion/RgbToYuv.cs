using System.Buffers;
using PictureSharp.Primitives.Parallelism;

namespace PictureSharp.Algorithms.ColorConversion;

internal sealed class RgbToYuv : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public int UvWidth => (Width + 1) >> 1;
    public int UvHeight => (Height + 1) >> 1;
    public byte[] Y { get; }
    public byte[] U { get; }
    public byte[] V { get; }

    public RgbToYuv(int width, int height)
    {
        Width = width; Height = height;
        Y = ArrayPool<byte>.Shared.Rent(checked(width * height));
        U = ArrayPool<byte>.Shared.Rent(UvWidth * UvHeight);
        V = ArrayPool<byte>.Shared.Rent(UvWidth * UvHeight);
    }

    /// <summary>
    /// Converts <paramref name="pixels"/>. Without dithering, pairs of rows are independent and bands of them may run
    /// in parallel; dithering draws random numbers in raster order, so it stays sequential.
    /// </summary>
    public void Convert(ReadOnlySpan<Rgba32> pixels, float dithering = 0) =>
        ConvertRows(pixels, 0, Height, dithering > 0 ? new DitherRandom(dithering) : null);

    /// <inheritdoc cref="Convert(ReadOnlySpan{Rgba32}, float)"/>
    public void Convert(Rgba32[] pixels, float dithering, int maxDegreeOfParallelism, CancellationToken cancellationToken = default)
    {
        const int band = 32;
        int bands = (Height + band - 1) / band;
        if (dithering <= 0 && bands > 1 && Parallelism.IsWorthwhile(maxDegreeOfParallelism, (long)Width * Height, 1 << 18))
        {
            Parallel.For(0, bands, Parallelism.Options(maxDegreeOfParallelism, cancellationToken),
                b => ConvertRows(pixels, b * band, Math.Min(Height, (b + 1) * band), null));
        }
        else
        {
            ConvertRows(pixels, 0, Height, dithering > 0 ? new DitherRandom(dithering) : null);
        }
    }

    // port: src/enc/picture_csp_enc.c ImportYUVAFromRGBA, AccumulateRGB, AccumulateRGBA ('first' is even)
    private void ConvertRows(ReadOnlySpan<Rgba32> pixels, int first, int last, DitherRandom? random)
    {
        for (int y = first; y < last; y += 2)
        {
            int end = Math.Min(y + 2, Height);
            for (int row = y; row < end; row++)
                for (int x = 0; x < Width; x++)
                {
                    Rgba32 p = pixels[row * Width + x];
                    Y[row * Width + x] = (byte)((16839 * p.R + 33059 * p.G + 6420 * p.B + (random?.Next(16) ?? 32768) + (16 << 16)) >> 16);
                }
            for (int x = 0; x < Width; x += 2)
            {
                Rgba32 p0 = pixels[y * Width + x], p1 = pixels[y * Width + Math.Min(x + 1, Width - 1)];
                Rgba32 p2 = pixels[Math.Min(y + 1, Height - 1) * Width + x], p3 = pixels[Math.Min(y + 1, Height - 1) * Width + Math.Min(x + 1, Width - 1)];
                int a = p0.A + p1.A + p2.A + p3.A;
                int r = Average(p0.R, p1.R, p2.R, p3.R, p0.A, p1.A, p2.A, p3.A, a);
                int g = Average(p0.G, p1.G, p2.G, p3.G, p0.A, p1.A, p2.A, p3.A, a);
                int b = Average(p0.B, p1.B, p2.B, p3.B, p0.A, p1.A, p2.A, p3.A, a);
                int i = (y >> 1) * UvWidth + (x >> 1);
                U[i] = (byte)Math.Clamp((-9719 * r - 19081 * g + 28800 * b + (random?.Next(18) ?? 131072) + (128 << 18)) >> 18, 0, 255);
                V[i] = (byte)Math.Clamp((28800 * r - 24116 * g - 4684 * b + (random?.Next(18) ?? 131072) + (128 << 18)) >> 18, 0, 255);
            }
        }
    }

    // port: src/enc/picture_csp_enc.c LinearToGammaWeighted, LinearToGamma, Interpolate
    private static int Average(int c0, int c1, int c2, int c3, int a0, int a1, int a2, int a3, int alpha)
    {
        uint sum = alpha is 0 or 1020 ? (uint)(GammaToLinear[c0] + GammaToLinear[c1] + GammaToLinear[c2] + GammaToLinear[c3])
            : (uint)(a0 * GammaToLinear[c0] + a1 * GammaToLinear[c1] + a2 * GammaToLinear[c2] + a3 * GammaToLinear[c3]) * InverseAlpha[alpha] >> 17;
        int index = (int)(sum >> 9), fraction = (int)(sum & 511);
        return (LinearToGamma[index + 1] * fraction + LinearToGamma[index] * (512 - fraction) + 64) >> 7;
    }

    // port: src/enc/picture_tools_enc.c WebPCleanupTransparentArea, SmoothenBlock
    public void CleanupTransparent(ReadOnlySpan<Rgba32> pixels)
    {
        for (int y = 0; y < Height; y += 8)
        {
            bool reset = true;
            byte luma = 0, u = 0, v = 0;
            for (int x = 0; x < Width; x += 8)
            {
                int w = Math.Min(8, Width - x), h = Math.Min(8, Height - y), sum = 0, count = 0;
                for (int j = 0; j < h; j++)
                    for (int i = 0; i < w; i++)
                    {
                        int pos = (y + j) * Width + x + i;
                        if (pixels[pos].A != 0) { count++; sum += Y[pos]; }
                    }
                if (count == 0 && w == 8 && h == 8)
                {
                    if (reset) { luma = Y[y * Width + x]; u = U[(y >> 1) * UvWidth + (x >> 1)]; v = V[(y >> 1) * UvWidth + (x >> 1)]; reset = false; }
                    for (int j = 0; j < 8; j++) Y.AsSpan((y + j) * Width + x, 8).Fill(luma);
                    for (int j = 0; j < 4; j++)
                    {
                        U.AsSpan(((y >> 1) + j) * UvWidth + (x >> 1), 4).Fill(u);
                        V.AsSpan(((y >> 1) + j) * UvWidth + (x >> 1), 4).Fill(v);
                    }
                }
                else
                {
                    reset = true;
                    if (count == 0 || count == w * h) continue;
                    byte average = (byte)(sum / count);
                    for (int j = 0; j < h; j++)
                        for (int i = 0; i < w; i++)
                        {
                            int pos = (y + j) * Width + x + i;
                            if (pixels[pos].A == 0) Y[pos] = average;
                        }
                }
            }
        }
    }

    public void Dispose() { ArrayPool<byte>.Shared.Return(Y); ArrayPool<byte>.Shared.Return(U); ArrayPool<byte>.Shared.Return(V); }

    // port: src/utils/random_utils.c VP8InitRandom; src/utils/random_utils.h VP8RandomBits2
    private sealed class DitherRandom(float amplitude)
    {
        private readonly uint[] _state = RandomTable.ToArray();
        private readonly int _amplitude = (int)(Math.Clamp(amplitude, 0, 1) * 256);
        private int _first, _second = 31;
        public int Next(int bits)
        {
            int diff = (int)(_state[_first] - _state[_second]);
            if (diff < 0) diff = (int)((uint)diff + (1u << 31));
            _state[_first] = (uint)diff;
            if (++_first == 55) _first = 0;
            if (++_second == 55) _second = 0;
            diff = (diff << 1) >> (32 - bits);
            return ((diff * _amplitude) >> 8) + (1 << (bits - 1));
        }
    }

    // port: src/enc/picture_csp_enc.c InitGammaTables, kInvAlpha
    private static ReadOnlySpan<int> GammaToLinear => [
        0, 49, 85, 117, 147, 176, 204, 231, 257, 282, 307, 331,
        355, 379, 402, 425, 447, 469, 491, 513, 534, 556, 577, 598,
        618, 639, 659, 679, 699, 719, 739, 759, 778, 798, 817, 836,
        855, 874, 893, 912, 930, 949, 967, 986, 1004, 1022, 1040, 1059,
        1077, 1094, 1112, 1130, 1148, 1165, 1183, 1200, 1218, 1235, 1252, 1270,
        1287, 1304, 1321, 1338, 1355, 1372, 1389, 1406, 1422, 1439, 1456, 1472,
        1489, 1505, 1522, 1538, 1555, 1571, 1587, 1604, 1620, 1636, 1652, 1668,
        1684, 1700, 1716, 1732, 1748, 1764, 1780, 1796, 1812, 1827, 1843, 1859,
        1874, 1890, 1905, 1921, 1937, 1952, 1967, 1983, 1998, 2014, 2029, 2044,
        2059, 2075, 2090, 2105, 2120, 2135, 2151, 2166, 2181, 2196, 2211, 2226,
        2241, 2256, 2270, 2285, 2300, 2315, 2330, 2345, 2359, 2374, 2389, 2403,
        2418, 2433, 2447, 2462, 2477, 2491, 2506, 2520, 2535, 2549, 2564, 2578,
        2592, 2607, 2621, 2636, 2650, 2664, 2679, 2693, 2707, 2721, 2736, 2750,
        2764, 2778, 2792, 2806, 2820, 2835, 2849, 2863, 2877, 2891, 2905, 2919,
        2933, 2947, 2961, 2975, 2988, 3002, 3016, 3030, 3044, 3058, 3072, 3085,
        3099, 3113, 3127, 3140, 3154, 3168, 3182, 3195, 3209, 3222, 3236, 3250,
        3263, 3277, 3291, 3304, 3318, 3331, 3345, 3358, 3372, 3385, 3399, 3412,
        3426, 3439, 3452, 3466, 3479, 3493, 3506, 3519, 3533, 3546, 3559, 3573,
        3586, 3599, 3612, 3626, 3639, 3652, 3665, 3678, 3692, 3705, 3718, 3731,
        3744, 3757, 3771, 3784, 3797, 3810, 3823, 3836, 3849, 3862, 3875, 3888,
        3901, 3914, 3927, 3940, 3953, 3966, 3979, 3992, 4005, 4018, 4031, 4044,
        4056, 4069, 4082, 4095,
    ];
    private static ReadOnlySpan<int> LinearToGamma => [
        0, 3, 8, 13, 19, 25, 31, 38, 45, 52, 60, 67,
        75, 83, 91, 99, 107, 116, 124, 133, 142, 151, 160, 169,
        178, 187, 197, 206, 216, 226, 235, 245, 255,
    ];
    private static ReadOnlySpan<uint> InverseAlpha => [
  0,  /* alpha = 0 */
  524288, 262144, 174762, 131072, 104857, 87381, 74898, 65536,
  58254, 52428, 47662, 43690, 40329, 37449, 34952, 32768,
  30840, 29127, 27594, 26214, 24966, 23831, 22795, 21845,
  20971, 20164, 19418, 18724, 18078, 17476, 16912, 16384,
  15887, 15420, 14979, 14563, 14169, 13797, 13443, 13107,
  12787, 12483, 12192, 11915, 11650, 11397, 11155, 10922,
  10699, 10485, 10280, 10082, 9892, 9709, 9532, 9362,
  9198, 9039, 8886, 8738, 8594, 8456, 8322, 8192,
  8065, 7943, 7825, 7710, 7598, 7489, 7384, 7281,
  7182, 7084, 6990, 6898, 6808, 6721, 6636, 6553,
  6472, 6393, 6316, 6241, 6168, 6096, 6026, 5957,
  5890, 5825, 5761, 5698, 5637, 5577, 5518, 5461,
  5405, 5349, 5295, 5242, 5190, 5140, 5090, 5041,
  4993, 4946, 4899, 4854, 4809, 4766, 4723, 4681,
  4639, 4599, 4559, 4519, 4481, 4443, 4405, 4369,
  4332, 4297, 4262, 4228, 4194, 4161, 4128, 4096,
  4064, 4032, 4002, 3971, 3942, 3912, 3883, 3855,
  3826, 3799, 3771, 3744, 3718, 3692, 3666, 3640,
  3615, 3591, 3566, 3542, 3518, 3495, 3472, 3449,
  3426, 3404, 3382, 3360, 3339, 3318, 3297, 3276,
  3256, 3236, 3216, 3196, 3177, 3158, 3139, 3120,
  3102, 3084, 3066, 3048, 3030, 3013, 2995, 2978,
  2962, 2945, 2928, 2912, 2896, 2880, 2864, 2849,
  2833, 2818, 2803, 2788, 2774, 2759, 2744, 2730,
  2716, 2702, 2688, 2674, 2661, 2647, 2634, 2621,
  2608, 2595, 2582, 2570, 2557, 2545, 2532, 2520,
  2508, 2496, 2484, 2473, 2461, 2449, 2438, 2427,
  2416, 2404, 2394, 2383, 2372, 2361, 2351, 2340,
  2330, 2319, 2309, 2299, 2289, 2279, 2269, 2259,
  2250, 2240, 2231, 2221, 2212, 2202, 2193, 2184,
  2175, 2166, 2157, 2148, 2139, 2131, 2122, 2114,
  2105, 2097, 2088, 2080, 2072, 2064, 2056, 2048,
  2040, 2032, 2024, 2016, 2008, 2001, 1993, 1985,
  1978, 1971, 1963, 1956, 1949, 1941, 1934, 1927,
  1920, 1913, 1906, 1899, 1892, 1885, 1879, 1872,
  1865, 1859, 1852, 1846, 1839, 1833, 1826, 1820,
  1814, 1807, 1801, 1795, 1789, 1783, 1777, 1771,
  1765, 1759, 1753, 1747, 1741, 1736, 1730, 1724,
  1718, 1713, 1707, 1702, 1696, 1691, 1685, 1680,
  1675, 1669, 1664, 1659, 1653, 1648, 1643, 1638,
  1633, 1628, 1623, 1618, 1613, 1608, 1603, 1598,
  1593, 1588, 1583, 1579, 1574, 1569, 1565, 1560,
  1555, 1551, 1546, 1542, 1537, 1533, 1528, 1524,
  1519, 1515, 1510, 1506, 1502, 1497, 1493, 1489,
  1485, 1481, 1476, 1472, 1468, 1464, 1460, 1456,
  1452, 1448, 1444, 1440, 1436, 1432, 1428, 1424,
  1420, 1416, 1413, 1409, 1405, 1401, 1398, 1394,
  1390, 1387, 1383, 1379, 1376, 1372, 1368, 1365,
  1361, 1358, 1354, 1351, 1347, 1344, 1340, 1337,
  1334, 1330, 1327, 1323, 1320, 1317, 1314, 1310,
  1307, 1304, 1300, 1297, 1294, 1291, 1288, 1285,
  1281, 1278, 1275, 1272, 1269, 1266, 1263, 1260,
  1257, 1254, 1251, 1248, 1245, 1242, 1239, 1236,
  1233, 1230, 1227, 1224, 1222, 1219, 1216, 1213,
  1210, 1208, 1205, 1202, 1199, 1197, 1194, 1191,
  1188, 1186, 1183, 1180, 1178, 1175, 1172, 1170,
  1167, 1165, 1162, 1159, 1157, 1154, 1152, 1149,
  1147, 1144, 1142, 1139, 1137, 1134, 1132, 1129,
  1127, 1125, 1122, 1120, 1117, 1115, 1113, 1110,
  1108, 1106, 1103, 1101, 1099, 1096, 1094, 1092,
  1089, 1087, 1085, 1083, 1081, 1078, 1076, 1074,
  1072, 1069, 1067, 1065, 1063, 1061, 1059, 1057,
  1054, 1052, 1050, 1048, 1046, 1044, 1042, 1040,
  1038, 1036, 1034, 1032, 1030, 1028, 1026, 1024,
  1022, 1020, 1018, 1016, 1014, 1012, 1010, 1008,
  1006, 1004, 1002, 1000, 998, 996, 994, 992,
  991, 989, 987, 985, 983, 981, 979, 978,
  976, 974, 972, 970, 969, 967, 965, 963,
  961, 960, 958, 956, 954, 953, 951, 949,
  948, 946, 944, 942, 941, 939, 937, 936,
  934, 932, 931, 929, 927, 926, 924, 923,
  921, 919, 918, 916, 914, 913, 911, 910,
  908, 907, 905, 903, 902, 900, 899, 897,
  896, 894, 893, 891, 890, 888, 887, 885,
  884, 882, 881, 879, 878, 876, 875, 873,
  872, 870, 869, 868, 866, 865, 863, 862,
  860, 859, 858, 856, 855, 853, 852, 851,
  849, 848, 846, 845, 844, 842, 841, 840,
  838, 837, 836, 834, 833, 832, 830, 829,
  828, 826, 825, 824, 823, 821, 820, 819,
  817, 816, 815, 814, 812, 811, 810, 809,
  807, 806, 805, 804, 802, 801, 800, 799,
  798, 796, 795, 794, 793, 791, 790, 789,
  788, 787, 786, 784, 783, 782, 781, 780,
  779, 777, 776, 775, 774, 773, 772, 771,
  769, 768, 767, 766, 765, 764, 763, 762,
  760, 759, 758, 757, 756, 755, 754, 753,
  752, 751, 750, 748, 747, 746, 745, 744,
  743, 742, 741, 740, 739, 738, 737, 736,
  735, 734, 733, 732, 731, 730, 729, 728,
  727, 726, 725, 724, 723, 722, 721, 720,
  719, 718, 717, 716, 715, 714, 713, 712,
  711, 710, 709, 708, 707, 706, 705, 704,
  703, 702, 701, 700, 699, 699, 698, 697,
  696, 695, 694, 693, 692, 691, 690, 689,
  688, 688, 687, 686, 685, 684, 683, 682,
  681, 680, 680, 679, 678, 677, 676, 675,
  674, 673, 673, 672, 671, 670, 669, 668,
  667, 667, 666, 665, 664, 663, 662, 661,
  661, 660, 659, 658, 657, 657, 656, 655,
  654, 653, 652, 652, 651, 650, 649, 648,
  648, 647, 646, 645, 644, 644, 643, 642,
  641, 640, 640, 639, 638, 637, 637, 636,
  635, 634, 633, 633, 632, 631, 630, 630,
  629, 628, 627, 627, 626, 625, 624, 624,
  623, 622, 621, 621, 620, 619, 618, 618,
  617, 616, 616, 615, 614, 613, 613, 612,
  611, 611, 610, 609, 608, 608, 607, 606,
  606, 605, 604, 604, 603, 602, 601, 601,
  600, 599, 599, 598, 597, 597, 596, 595,
  595, 594, 593, 593, 592, 591, 591, 590,
  589, 589, 588, 587, 587, 586, 585, 585,
  584, 583, 583, 582, 581, 581, 580, 579,
  579, 578, 578, 577, 576, 576, 575, 574,
  574, 573, 572, 572, 571, 571, 570, 569,
  569, 568, 568, 567, 566, 566, 565, 564,
  564, 563, 563, 562, 561, 561, 560, 560,
  559, 558, 558, 557, 557, 556, 555, 555,
  554, 554, 553, 553, 552, 551, 551, 550,
  550, 549, 548, 548, 547, 547, 546, 546,
  545, 544, 544, 543, 543, 542, 542, 541,
  541, 540, 539, 539, 538, 538, 537, 537,
  536, 536, 535, 534, 534, 533, 533, 532,
  532, 531, 531, 530, 530, 529, 529, 528,
  527, 527, 526, 526, 525, 525, 524, 524,
  523, 523, 522, 522, 521, 521, 520, 520,
  519, 519, 518, 518, 517, 517, 516, 516,
  515, 515, 514, 514
];
    private static ReadOnlySpan<uint> RandomTable => [
  0x0de15230, 0x03b31886, 0x775faccb, 0x1c88626a, 0x68385c55, 0x14b3b828,
  0x4a85fef8, 0x49ddb84b, 0x64fcf397, 0x5c550289, 0x4a290000, 0x0d7ec1da,
  0x5940b7ab, 0x5492577d, 0x4e19ca72, 0x38d38c69, 0x0c01ee65, 0x32a1755f,
  0x5437f652, 0x5abb2c32, 0x0faa57b1, 0x73f533e7, 0x685feeda, 0x7563cce2,
  0x6e990e83, 0x4730a7ed, 0x4fc0d9c6, 0x496b153c, 0x4f1403fa, 0x541afb0c,
  0x73990b32, 0x26d7cb1c, 0x6fcc3706, 0x2cbb77d8, 0x75762f2a, 0x6425ccdd,
  0x24b35461, 0x0a7d8715, 0x220414a8, 0x141ebf67, 0x56b41583, 0x73e502e3,
  0x44cab16f, 0x28264d42, 0x73baaefb, 0x0a50ebed, 0x1d6ab6fb, 0x0d3ad40b,
  0x35db3b68, 0x2b081e83, 0x77ce6b95, 0x5181e5f0, 0x78853bbc, 0x009f9494,
  0x27e5ed3c
];
}
