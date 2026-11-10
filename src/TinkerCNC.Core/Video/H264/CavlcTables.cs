namespace TinkerCNC.Video.H264;

/// <summary>A variable-length code: its bit pattern, right-aligned, and how many bits of it are used.</summary>
internal readonly record struct Vlc(byte Length, ushort Code);

/// <summary>
/// The variable-length code tables of CAVLC entropy coding (H.264 clause 9.2): coeff_token by the number of
/// coefficients in the neighbouring blocks (nC), total_zeros and run_before.
/// </summary>
internal static class CavlcTables
{
    /// <summary>coeff_token for 0 ≤ nC &lt; 2, 2 ≤ nC &lt; 4, 4 ≤ nC &lt; 8 and nC ≥ 8, indexed [table][TotalCoeff][TrailingOnes].</summary>
    public static readonly Vlc[][][] CoeffToken =
    [
        Rows(
            [1, 1],
            [6, 5, 2, 1],
            [8, 7, 6, 4, 3, 1],
            [9, 7, 8, 6, 7, 5, 5, 3],
            [10, 7, 9, 6, 8, 5, 6, 3],
            [11, 7, 10, 6, 9, 5, 7, 4],
            [13, 15, 11, 6, 10, 5, 8, 4],
            [13, 11, 13, 14, 11, 5, 9, 4],
            [13, 8, 13, 10, 13, 13, 10, 4],
            [14, 15, 14, 14, 13, 9, 11, 4],
            [14, 11, 14, 10, 14, 13, 13, 12],
            [15, 15, 15, 14, 14, 9, 14, 12],
            [15, 11, 15, 10, 15, 13, 14, 8],
            [16, 15, 15, 1, 15, 9, 15, 12],
            [16, 11, 16, 14, 16, 13, 15, 8],
            [16, 7, 16, 10, 16, 9, 16, 12],
            [16, 4, 16, 6, 16, 5, 16, 8]),
        Rows(
            [2, 3],
            [6, 11, 2, 2],
            [6, 7, 5, 7, 3, 3],
            [7, 7, 6, 10, 6, 9, 4, 5],
            [8, 7, 6, 6, 6, 5, 4, 4],
            [8, 4, 7, 6, 7, 5, 5, 6],
            [9, 7, 8, 6, 8, 5, 6, 8],
            [11, 15, 9, 6, 9, 5, 6, 4],
            [11, 11, 11, 14, 11, 13, 7, 4],
            [12, 15, 11, 10, 11, 9, 9, 4],
            [12, 11, 12, 14, 12, 13, 11, 12],
            [12, 8, 12, 10, 12, 9, 11, 8],
            [13, 15, 13, 14, 13, 13, 12, 12],
            [13, 11, 13, 10, 13, 9, 13, 12],
            [13, 7, 14, 11, 13, 6, 13, 8],
            [14, 9, 14, 8, 14, 10, 13, 1],
            [14, 7, 14, 6, 14, 5, 14, 4]),
        Rows(
            [4, 15],
            [6, 15, 4, 14],
            [6, 11, 5, 15, 4, 13],
            [6, 8, 5, 12, 5, 14, 4, 12],
            [7, 15, 5, 10, 5, 11, 4, 11],
            [7, 11, 5, 8, 5, 9, 4, 10],
            [7, 9, 6, 14, 6, 13, 4, 9],
            [7, 8, 6, 10, 6, 9, 4, 8],
            [8, 15, 7, 14, 7, 13, 5, 13],
            [8, 11, 8, 14, 7, 10, 6, 12],
            [9, 15, 8, 10, 8, 13, 7, 12],
            [9, 11, 9, 14, 8, 9, 8, 12],
            [9, 8, 9, 10, 9, 13, 8, 8],
            [10, 13, 9, 7, 9, 9, 9, 12],
            [10, 9, 10, 12, 10, 11, 10, 10],
            [10, 5, 10, 8, 10, 7, 10, 6],
            [10, 1, 10, 4, 10, 3, 10, 2]),
        FixedLengthCoeffToken(),
    ];

    /// <summary>coeff_token of the 2×2 chroma DC blocks (nC = −1), indexed [TotalCoeff][TrailingOnes].</summary>
    public static readonly Vlc[][] ChromaDcCoeffToken = Rows(
        [2, 1],
        [6, 7, 1, 1],
        [6, 4, 6, 6, 3, 1],
        [6, 3, 7, 3, 7, 2, 6, 5],
        [6, 2, 8, 3, 8, 2, 7, 0]);

    /// <summary>total_zeros of 4×4 blocks, indexed [TotalCoeff − 1][total_zeros].</summary>
    public static readonly Vlc[][] TotalZeros = Rows(
        [1, 1, 3, 3, 3, 2, 4, 3, 4, 2, 5, 3, 5, 2, 6, 3, 6, 2, 7, 3, 7, 2, 8, 3, 8, 2, 9, 3, 9, 2, 9, 1],
        [3, 7, 3, 6, 3, 5, 3, 4, 3, 3, 4, 5, 4, 4, 4, 3, 4, 2, 5, 3, 5, 2, 6, 3, 6, 2, 6, 1, 6, 0],
        [4, 5, 3, 7, 3, 6, 3, 5, 4, 4, 4, 3, 3, 4, 3, 3, 4, 2, 5, 3, 5, 2, 6, 1, 5, 1, 6, 0],
        [5, 3, 3, 7, 4, 5, 4, 4, 3, 6, 3, 5, 3, 4, 4, 3, 3, 3, 4, 2, 5, 2, 5, 1, 5, 0],
        [4, 5, 4, 4, 4, 3, 3, 7, 3, 6, 3, 5, 3, 4, 3, 3, 4, 2, 5, 1, 4, 1, 5, 0],
        [6, 1, 5, 1, 3, 7, 3, 6, 3, 5, 3, 4, 3, 3, 3, 2, 4, 1, 3, 1, 6, 0],
        [6, 1, 5, 1, 3, 5, 3, 4, 3, 3, 2, 3, 3, 2, 4, 1, 3, 1, 6, 0],
        [6, 1, 4, 1, 5, 1, 3, 3, 2, 3, 2, 2, 3, 2, 3, 1, 6, 0],
        [6, 1, 6, 0, 4, 1, 2, 3, 2, 2, 3, 1, 2, 1, 5, 1],
        [5, 1, 5, 0, 3, 1, 2, 3, 2, 2, 2, 1, 4, 1],
        [4, 0, 4, 1, 3, 1, 3, 2, 1, 1, 3, 3],
        [4, 0, 4, 1, 2, 1, 1, 1, 3, 1],
        [3, 0, 3, 1, 1, 1, 2, 1],
        [2, 0, 2, 1, 1, 1],
        [1, 0, 1, 1]);

    /// <summary>total_zeros of 2×2 chroma DC blocks, indexed [TotalCoeff − 1][total_zeros].</summary>
    public static readonly Vlc[][] ChromaDcTotalZeros = Rows(
        [1, 1, 2, 1, 3, 1, 3, 0],
        [1, 1, 2, 1, 2, 0],
        [1, 1, 1, 0]);

    /// <summary>run_before, indexed [min(zerosLeft, 7) − 1][run_before].</summary>
    public static readonly Vlc[][] RunBefore = Rows(
        [1, 1, 1, 0],
        [1, 1, 2, 1, 2, 0],
        [2, 3, 2, 2, 2, 1, 2, 0],
        [2, 3, 2, 2, 2, 1, 3, 1, 3, 0],
        [2, 3, 2, 2, 3, 3, 3, 2, 3, 1, 3, 0],
        [2, 3, 3, 0, 3, 1, 3, 3, 3, 2, 3, 5, 3, 4],
        [3, 7, 3, 6, 3, 5, 3, 4, 3, 3, 3, 2, 3, 1, 4, 1, 5, 1, 6, 1, 7, 1, 8, 1, 9, 1, 10, 1, 11, 1]);

    /// <summary>Which coeff_token table nC selects (nC ≥ 0).</summary>
    public static int CoeffTokenTableFor(int nC) => nC < 2 ? 0 : nC < 4 ? 1 : nC < 8 ? 2 : 3;

    /// <summary>Rows of (length, code) pairs.</summary>
    private static Vlc[][] Rows(params int[][] rows) => rows.Select(Row).ToArray();

    private static Vlc[] Row(int[] pairs)
    {
        var row = new Vlc[pairs.Length / 2];
        for (var i = 0; i < row.Length; i++) row[i] = new Vlc((byte)pairs[2 * i], (ushort)pairs[2 * i + 1]);
        return row;
    }

    /// <summary>For nC ≥ 8 coeff_token is a 6-bit fixed-length code: 4 bits TotalCoeff − 1, 2 bits TrailingOnes; no coefficients is 000011.</summary>
    private static Vlc[][] FixedLengthCoeffToken()
    {
        var table = new Vlc[17][];
        table[0] = [new Vlc(6, 3)];
        for (var total = 1; total <= 16; total++)
        {
            var row = new Vlc[Math.Min(total, 3) + 1];
            for (var ones = 0; ones < row.Length; ones++) row[ones] = new Vlc(6, (ushort)(((total - 1) << 2) | ones));
            table[total] = row;
        }
        return table;
    }
}
