namespace TinkerCNC.Video.H264;

/// <summary>Writes the coefficients of one transform block with CAVLC (H.264 clause 9.2).</summary>
internal static class CavlcWriter
{
    /// <summary>nC of the 2×2 chroma DC blocks, which have their own coeff_token table.</summary>
    public const int ChromaDc = -1;

    /// <summary>Largest coefficient magnitude written; above it the level code would need the long escape that
    /// the Baseline profile forbids, so quantizers clamp to it.</summary>
    public const int MaxLevel = 2063;

    /// <summary>
    /// Writes a block whose coefficients are given in scan order and returns TotalCoeff, the number of non-zero
    /// coefficients, which neighbouring blocks need for their nC.
    /// </summary>
    public static int WriteBlock(BitWriter w, ReadOnlySpan<int> coefficients, int nC)
    {
        Span<int> levels = stackalloc int[16];
        Span<int> runs = stackalloc int[16];
        var total = 0;
        var last = -1;
        for (var i = 0; i < coefficients.Length; i++)
        {
            if (coefficients[i] == 0) continue;
            total++;
            last = i;
        }

        // Non-zero coefficients from the highest frequency down, each with the zeros right before it.
        var zeros = 0;
        var n = 0;
        for (var i = last - 1; i >= -1; i--)
        {
            if (i >= 0 && coefficients[i] == 0)
            {
                zeros++;
                continue;
            }
            var position = FindNonZeroAbove(coefficients, i);
            levels[n] = coefficients[position];
            runs[n] = zeros;
            n++;
            zeros = 0;
        }

        var trailingOnes = 0;
        while (trailingOnes < Math.Min(3, total) && Math.Abs(levels[trailingOnes]) == 1) trailingOnes++;

        WriteCoeffToken(w, total, trailingOnes, nC);
        if (total == 0) return 0;

        for (var i = 0; i < trailingOnes; i++) w.WriteBit(levels[i] < 0 ? 1 : 0);

        var suffixLength = total > 10 && trailingOnes < 3 ? 1 : 0;
        for (var i = trailingOnes; i < total; i++)
        {
            var level = levels[i];
            var levelCode = level > 0 ? 2 * level - 2 : -2 * level - 1;
            if (i == trailingOnes && trailingOnes < 3) levelCode -= 2;
            WriteLevel(w, levelCode, suffixLength);
            if (suffixLength == 0) suffixLength = 1;
            if (Math.Abs(level) > 3 << (suffixLength - 1) && suffixLength < 6) suffixLength++;
        }

        if (total < coefficients.Length)
        {
            var totalZeros = last + 1 - total;
            var zerosLeft = totalZeros;
            Write(w, nC == ChromaDc ? CavlcTables.ChromaDcTotalZeros[total - 1][totalZeros] : CavlcTables.TotalZeros[total - 1][totalZeros]);
            for (var i = 0; i < total - 1 && zerosLeft > 0; i++)
            {
                Write(w, CavlcTables.RunBefore[Math.Min(zerosLeft, 7) - 1][runs[i]]);
                zerosLeft -= runs[i];
            }
        }
        return total;
    }

    /// <summary>The nearest non-zero coefficient at or above <paramref name="below"/>: the one whose run of zeros just ended.</summary>
    private static int FindNonZeroAbove(ReadOnlySpan<int> coefficients, int below)
    {
        var i = below + 1;
        while (coefficients[i] == 0) i++;
        return i;
    }

    private static void WriteCoeffToken(BitWriter w, int total, int trailingOnes, int nC) =>
        Write(w, nC == ChromaDc
            ? CavlcTables.ChromaDcCoeffToken[total][trailingOnes]
            : CavlcTables.CoeffToken[CavlcTables.CoeffTokenTableFor(nC)][total][trailingOnes]);

    /// <summary>level_prefix (zeros and a one) and level_suffix for one coefficient.</summary>
    private static void WriteLevel(BitWriter w, int levelCode, int suffixLength)
    {
        if (suffixLength == 0)
        {
            if (levelCode < 14)
            {
                WritePrefix(w, levelCode);
            }
            else if (levelCode < 30)
            {
                WritePrefix(w, 14);
                w.Write((uint)(levelCode - 14), 4);
            }
            else
            {
                WritePrefix(w, 15);
                w.Write((uint)(levelCode - 30), 12);
            }
            return;
        }

        var prefix = levelCode >> suffixLength;
        if (prefix < 15)
        {
            WritePrefix(w, prefix);
            w.Write((uint)(levelCode & ((1 << suffixLength) - 1)), suffixLength);
        }
        else
        {
            WritePrefix(w, 15);
            w.Write((uint)(levelCode - (15 << suffixLength)), 12);
        }
    }

    private static void WritePrefix(BitWriter w, int zeros) => w.Write(1, zeros + 1);

    private static void Write(BitWriter w, Vlc code) => w.Write(code.Code, code.Length);
}
