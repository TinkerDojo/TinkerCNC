using System.Numerics;

namespace TinkerCNC.Video.H264;

/// <summary>
/// Codes the macroblocks of one picture as a single slice: Intra_16x16 macroblocks in I slices; in P slices
/// macroblocks copied from the previous picture (P_Skip), copied with a coded difference (P_L0_16x16 without
/// motion) or Intra_16x16 where that is clearly cheaper. Each macroblock is reconstructed exactly as a decoder
/// would, so the next picture predicts from what the viewer will see.
/// </summary>
internal sealed class SliceCoder
{
    /// <summary>Above this difference to the previous picture (sum over the 256 luma samples) intra coding is tried.</summary>
    private const int IntraCheckThreshold = 768;

    /// <summary>Intra coding costs more bits, so its prediction must be better than the copy by this much.</summary>
    private const int IntraPenalty = 512;

    private const int IntraTypesInPSlice = 5;

    private static readonly int[] Zigzag = [0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15];

    // Position of the 16 luma 4×4 blocks, in coding order (8×8 quadrants, each in raster order), and the reverse.
    private static readonly int[] BlockX = [0, 1, 0, 1, 2, 3, 2, 3, 0, 1, 0, 1, 2, 3, 2, 3];
    private static readonly int[] BlockY = [0, 0, 1, 1, 0, 0, 1, 1, 2, 2, 3, 3, 2, 2, 3, 3];
    private static readonly int[] BlockIndexAt = [0, 1, 4, 5, 2, 3, 6, 7, 8, 9, 12, 13, 10, 11, 14, 15];

    // coded_block_pattern of inter macroblocks is sent as the code number that maps to it (Table 9-4).
    private static readonly int[] InterCbpCodeNum = Invert(
    [
        0, 16, 1, 2, 4, 8, 32, 3, 5, 10, 12, 15, 47, 7, 11, 13, 14, 6, 9, 31, 35, 37, 42, 44,
        33, 34, 36, 40, 39, 43, 45, 46, 17, 18, 20, 24, 19, 21, 26, 28, 23, 27, 29, 30, 22, 25, 38, 41,
    ]);

    private readonly int _mbWidth;
    private readonly int _mbHeight;

    // Non-zero coefficient counts of every 4×4 block of the picture, for the nC of later blocks.
    private readonly byte[] _countsLuma;
    private readonly byte[] _countsCb;
    private readonly byte[] _countsCr;

    // The macroblock being coded: source, prediction, reconstruction (luma 16×16; chroma two 8×8 planes).
    private readonly byte[] _sourceY = new byte[256];
    private readonly byte[] _predictionY = new byte[256];
    private readonly byte[] _candidateY = new byte[256];
    private readonly byte[] _reconstructedY = new byte[256];
    private readonly byte[] _sourceC = new byte[128];
    private readonly byte[] _predictionC = new byte[128];
    private readonly byte[] _candidateC = new byte[128];
    private readonly byte[] _reconstructedC = new byte[128];

    // Quantized levels: luma [block * 16 + position], the luma DC matrix, chroma [component * 64 + block * 16 + position], chroma DC.
    private readonly int[] _lumaLevels = new int[256];
    private readonly int[] _lumaDc = new int[16];
    private readonly int[] _chromaLevels = new int[128];
    private readonly int[] _chromaDc = new int[8];
    private readonly int[] _block = new int[16];
    private readonly int[] _coefficients = new int[16];
    private readonly int[] _scan = new int[16];

    public SliceCoder(int mbWidth, int mbHeight)
    {
        _mbWidth = mbWidth;
        _mbHeight = mbHeight;
        _countsLuma = new byte[mbWidth * mbHeight * 16];
        _countsCb = new byte[mbWidth * mbHeight * 4];
        _countsCr = new byte[mbWidth * mbHeight * 4];
    }

    /// <summary>Writes the slice data of <paramref name="source"/> and fills <paramref name="reconstruction"/>.</summary>
    /// <param name="reference">The previous reconstructed picture for a P slice; null for an I slice.</param>
    public void Encode(BitWriter w, YuvPicture source, YuvPicture reconstruction, YuvPicture? reference, Quantizer luma, Quantizer chroma)
    {
        Array.Clear(_countsLuma);
        Array.Clear(_countsCb);
        Array.Clear(_countsCr);
        var skipRun = 0;
        for (var mbY = 0; mbY < _mbHeight; mbY++)
            for (var mbX = 0; mbX < _mbWidth; mbX++)
            {
                Load(source, mbX, mbY, _sourceY, _sourceC);
                if (reference is null)
                {
                    CodeIntra(w, reconstruction, mbX, mbY, luma, chroma, inPSlice: false);
                    continue;
                }

                Load(reference, mbX, mbY, _predictionY, _predictionC);
                var copyDifference = Sad(_sourceY, _predictionY);
                if (copyDifference == 0 && Sad(_sourceC, _predictionC) == 0)
                {
                    // Nothing changed here: the copy is exact, no transform needed.
                    skipRun++;
                    Store(reconstruction, mbX, mbY, _predictionY, _predictionC);
                    continue;
                }
                if (copyDifference > IntraCheckThreshold
                    && BestIntraLuma(reconstruction, mbX, mbY, _candidateY, out _) + IntraPenalty < copyDifference)
                {
                    w.WriteUe((uint)skipRun);
                    skipRun = 0;
                    CodeIntra(w, reconstruction, mbX, mbY, luma, chroma, inPSlice: true);
                    continue;
                }

                var cbpLuma = CodeLumaInter(luma);
                var cbpChroma = CodeChroma(chroma, chroma.InterRounding);
                if (cbpLuma == 0 && cbpChroma == 0)
                {
                    skipRun++;
                    Store(reconstruction, mbX, mbY, _predictionY, _predictionC);
                    continue;
                }

                w.WriteUe((uint)skipRun);
                skipRun = 0;
                WriteInter(w, mbX, mbY, cbpLuma, cbpChroma);
                Store(reconstruction, mbX, mbY, _reconstructedY, _reconstructedC);
            }
        if (skipRun > 0) w.WriteUe((uint)skipRun);
    }

    private void CodeIntra(BitWriter w, YuvPicture reconstruction, int mbX, int mbY, Quantizer luma, Quantizer chroma, bool inPSlice)
    {
        BestIntraLuma(reconstruction, mbX, mbY, _predictionY, out var lumaMode);
        BestIntraChroma(reconstruction, mbX, mbY, out var chromaMode);
        var cbpLuma = CodeLumaIntra16(luma);
        var cbpChroma = CodeChroma(chroma, chroma.IntraRounding);

        var mbType = 1 + lumaMode + 4 * cbpChroma + (cbpLuma != 0 ? 12 : 0) + (inPSlice ? IntraTypesInPSlice : 0);
        w.WriteUe((uint)mbType);
        w.WriteUe((uint)chromaMode);        // intra_chroma_pred_mode
        w.WriteSe(0);                       // mb_qp_delta

        for (var k = 0; k < 16; k++) _scan[k] = _lumaDc[Zigzag[k]];
        CavlcWriter.WriteBlock(w, _scan, LumaNc(mbX, mbY, 0));
        for (var block = 0; block < 16; block++)
        {
            var count = 0;
            if (cbpLuma != 0)
            {
                var levels = _lumaLevels.AsSpan(block * 16, 16);
                for (var k = 1; k < 16; k++) _scan[k - 1] = levels[Zigzag[k]];
                count = CavlcWriter.WriteBlock(w, _scan.AsSpan(0, 15), LumaNc(mbX, mbY, block));
            }
            _countsLuma[(mbY * _mbWidth + mbX) * 16 + block] = (byte)count;
        }
        WriteChromaResidual(w, mbX, mbY, cbpChroma);
        Store(reconstruction, mbX, mbY, _reconstructedY, _reconstructedC);
    }

    private void WriteInter(BitWriter w, int mbX, int mbY, int cbpLuma, int cbpChroma)
    {
        w.WriteUe(0);                       // mb_type P_L0_16x16
        w.WriteSe(0);                       // mvd_l0 x: the prediction is the same place in the previous picture
        w.WriteSe(0);                       // mvd_l0 y
        w.WriteUe((uint)InterCbpCodeNum[cbpLuma | (cbpChroma << 4)]);
        w.WriteSe(0);                       // mb_qp_delta
        for (var block = 0; block < 16; block++)
        {
            var count = 0;
            if ((cbpLuma >> (block >> 2) & 1) != 0)
            {
                var levels = _lumaLevels.AsSpan(block * 16, 16);
                for (var k = 0; k < 16; k++) _scan[k] = levels[Zigzag[k]];
                count = CavlcWriter.WriteBlock(w, _scan, LumaNc(mbX, mbY, block));
            }
            _countsLuma[(mbY * _mbWidth + mbX) * 16 + block] = (byte)count;
        }
        WriteChromaResidual(w, mbX, mbY, cbpChroma);
    }

    private void WriteChromaResidual(BitWriter w, int mbX, int mbY, int cbpChroma)
    {
        if (cbpChroma == 0) return;
        for (var component = 0; component < 2; component++)
            CavlcWriter.WriteBlock(w, _chromaDc.AsSpan(component * 4, 4), CavlcWriter.ChromaDc);
        if (cbpChroma < 2) return;
        for (var component = 0; component < 2; component++)
        {
            var counts = component == 0 ? _countsCb : _countsCr;
            for (var block = 0; block < 4; block++)
            {
                var levels = _chromaLevels.AsSpan(component * 64 + block * 16, 16);
                for (var k = 1; k < 16; k++) _scan[k - 1] = levels[Zigzag[k]];
                var count = CavlcWriter.WriteBlock(w, _scan.AsSpan(0, 15), ChromaNc(counts, mbX, mbY, block));
                counts[(mbY * _mbWidth + mbX) * 4 + block] = (byte)count;
            }
        }
    }

    // ------------------------------------------------------------------ transform and quantization

    /// <summary>Codes the luma difference as Intra_16x16: AC levels per block, the DCs through the Hadamard transform. Returns the luma coded_block_pattern (0 or 15).</summary>
    private int CodeLumaIntra16(Quantizer q)
    {
        var cbp = 0;
        Span<int> dc = stackalloc int[16];
        for (var block = 0; block < 16; block++)
        {
            LoadLumaResidual(block);
            Transform.Forward4x4(_block, _coefficients);
            dc[BlockY[block] * 4 + BlockX[block]] = _coefficients[0];
            var levels = _lumaLevels.AsSpan(block * 16, 16);
            levels[0] = 0;
            for (var i = 1; i < 16; i++)
            {
                levels[i] = q.Quantize(_coefficients[i], i, q.IntraRounding);
                if (levels[i] != 0) cbp = 15;
            }
        }
        Transform.Hadamard4x4(dc);
        for (var i = 0; i < 16; i++) _lumaDc[i] = q.QuantizeLumaDc(dc[i], q.IntraRounding);

        _lumaDc.CopyTo(dc);
        Transform.Hadamard4x4(dc);
        for (var block = 0; block < 16; block++)
        {
            var levels = _lumaLevels.AsSpan(block * 16, 16);
            _block[0] = q.ScaledLumaDc(dc[BlockY[block] * 4 + BlockX[block]]);
            for (var i = 1; i < 16; i++) _block[i] = q.Scaled(levels[i], i);
            Transform.Inverse4x4(_block);
            AddLumaPrediction(block);
        }
        return cbp;
    }

    /// <summary>Codes the luma difference to the copied prediction block by block. Returns the luma coded_block_pattern (one bit per 8×8).</summary>
    private int CodeLumaInter(Quantizer q)
    {
        var cbp = 0;
        for (var block = 0; block < 16; block++)
        {
            LoadLumaResidual(block);
            Transform.Forward4x4(_block, _coefficients);
            var levels = _lumaLevels.AsSpan(block * 16, 16);
            for (var i = 0; i < 16; i++)
            {
                levels[i] = q.Quantize(_coefficients[i], i, q.InterRounding);
                if (levels[i] != 0) cbp |= 1 << (block >> 2);
            }
        }
        for (var block = 0; block < 16; block++)
        {
            var levels = _lumaLevels.AsSpan(block * 16, 16);
            for (var i = 0; i < 16; i++) _block[i] = q.Scaled(levels[i], i);
            Transform.Inverse4x4(_block);
            AddLumaPrediction(block);
        }
        return cbp;
    }

    /// <summary>Codes both chroma components. Returns the chroma coded_block_pattern: 0 nothing, 1 DC only, 2 DC and AC.</summary>
    private int CodeChroma(Quantizer q, int rounding)
    {
        var anyDc = false;
        var anyAc = false;
        for (var component = 0; component < 2; component++)
        {
            var dc = _chromaDc.AsSpan(component * 4, 4);
            for (var block = 0; block < 4; block++)
            {
                LoadChromaResidual(component, block);
                Transform.Forward4x4(_block, _coefficients);
                dc[block] = _coefficients[0];
                var levels = _chromaLevels.AsSpan(component * 64 + block * 16, 16);
                levels[0] = 0;
                for (var i = 1; i < 16; i++)
                {
                    levels[i] = q.Quantize(_coefficients[i], i, rounding);
                    anyAc |= levels[i] != 0;
                }
            }
            Transform.Hadamard2x2(dc);
            for (var i = 0; i < 4; i++)
            {
                dc[i] = q.QuantizeChromaDc(dc[i], rounding);
                anyDc |= dc[i] != 0;
            }
        }

        Span<int> scaledDc = stackalloc int[4];
        for (var component = 0; component < 2; component++)
        {
            _chromaDc.AsSpan(component * 4, 4).CopyTo(scaledDc);
            Transform.Hadamard2x2(scaledDc);
            for (var block = 0; block < 4; block++)
            {
                var levels = _chromaLevels.AsSpan(component * 64 + block * 16, 16);
                _block[0] = q.ScaledChromaDc(scaledDc[block]);
                for (var i = 1; i < 16; i++) _block[i] = q.Scaled(levels[i], i);
                Transform.Inverse4x4(_block);
                AddChromaPrediction(component, block);
            }
        }
        return anyAc ? 2 : anyDc ? 1 : 0;
    }

    private void LoadLumaResidual(int block)
    {
        var x0 = BlockX[block] * 4;
        var y0 = BlockY[block] * 4;
        for (var j = 0; j < 4; j++)
            for (var i = 0; i < 4; i++)
            {
                var p = (y0 + j) * 16 + x0 + i;
                _block[j * 4 + i] = _sourceY[p] - _predictionY[p];
            }
    }

    private void AddLumaPrediction(int block)
    {
        var x0 = BlockX[block] * 4;
        var y0 = BlockY[block] * 4;
        for (var j = 0; j < 4; j++)
            for (var i = 0; i < 4; i++)
            {
                var p = (y0 + j) * 16 + x0 + i;
                _reconstructedY[p] = (byte)Math.Clamp(_predictionY[p] + _block[j * 4 + i], 0, 255);
            }
    }

    private void LoadChromaResidual(int component, int block)
    {
        var x0 = (block & 1) * 4;
        var y0 = (block >> 1) * 4;
        for (var j = 0; j < 4; j++)
            for (var i = 0; i < 4; i++)
            {
                var p = component * 64 + (y0 + j) * 8 + x0 + i;
                _block[j * 4 + i] = _sourceC[p] - _predictionC[p];
            }
    }

    private void AddChromaPrediction(int component, int block)
    {
        var x0 = (block & 1) * 4;
        var y0 = (block >> 1) * 4;
        for (var j = 0; j < 4; j++)
            for (var i = 0; i < 4; i++)
            {
                var p = component * 64 + (y0 + j) * 8 + x0 + i;
                _reconstructedC[p] = (byte)Math.Clamp(_predictionC[p] + _block[j * 4 + i], 0, 255);
            }
    }

    // ------------------------------------------------------------------ prediction

    /// <summary>Tries the usable Intra_16x16 modes and keeps the one closest to the source. Returns its difference.</summary>
    private int BestIntraLuma(YuvPicture reconstruction, int mbX, int mbY, Span<byte> best, out int mode)
    {
        var above = mbY > 0;
        var left = mbX > 0;
        var bestSad = int.MaxValue;
        mode = 0;
        var modes = IntraModes.Luma16x16;
        for (var m = 0; m < modes.Length; m++)
        {
            if (!modes[m].CanPredict(above, left)) continue;
            modes[m].Predict(reconstruction.Y, reconstruction.PaddedWidth, mbX * 16, mbY * 16, above, left, _candidateY);
            var sad = Sad(_sourceY, _candidateY);
            if (sad >= bestSad) continue;
            bestSad = sad;
            mode = m;
            if (!best.Overlaps(_candidateY)) _candidateY.CopyTo(best);
        }
        if (best.Overlaps(_candidateY) && mode != modes.Length - 1)
            modes[mode].Predict(reconstruction.Y, reconstruction.PaddedWidth, mbX * 16, mbY * 16, above, left, best);
        return bestSad;
    }

    /// <summary>Tries the chroma modes on both components and fills the prediction with the best.</summary>
    private void BestIntraChroma(YuvPicture reconstruction, int mbX, int mbY, out int mode)
    {
        var above = mbY > 0;
        var left = mbX > 0;
        var bestSad = int.MaxValue;
        mode = 0;
        var modes = IntraModes.Chroma8x8;
        for (var m = 0; m < modes.Length; m++)
        {
            if (!modes[m].CanPredict(above, left)) continue;
            modes[m].Predict(reconstruction.Cb, reconstruction.ChromaWidth, mbX * 8, mbY * 8, above, left, _candidateC.AsSpan(0, 64));
            modes[m].Predict(reconstruction.Cr, reconstruction.ChromaWidth, mbX * 8, mbY * 8, above, left, _candidateC.AsSpan(64, 64));
            var sad = Sad(_sourceC, _candidateC);
            if (sad >= bestSad) continue;
            bestSad = sad;
            mode = m;
            _candidateC.CopyTo(_predictionC, 0);
        }
    }

    /// <summary>Sum of absolute differences of two equally long sample runs (multiples of the vector width).</summary>
    private static int Sad(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var sum = Vector<uint>.Zero;
        var i = 0;
        for (; i + Vector<byte>.Count <= a.Length; i += Vector<byte>.Count)
        {
            var va = new Vector<byte>(a.Slice(i));
            var vb = new Vector<byte>(b.Slice(i));
            var difference = Vector.Max(va, vb) - Vector.Min(va, vb);
            Vector.Widen(difference, out var low, out var high);
            Vector.Widen(low, out var l0, out var l1);
            Vector.Widen(high, out var h0, out var h1);
            sum += l0 + l1 + h0 + h1;
        }
        var total = (int)Vector.Sum(sum);
        for (; i < a.Length; i++) total += Math.Abs(a[i] - b[i]);
        return total;
    }

    // ------------------------------------------------------------------ nC of neighbouring blocks

    private int LumaNc(int mbX, int mbY, int block)
    {
        var bx = BlockX[block];
        var by = BlockY[block];
        var mb = mbY * _mbWidth + mbX;
        var hasLeft = bx > 0 || mbX > 0;
        var hasAbove = by > 0 || mbY > 0;
        var nA = !hasLeft ? 0 : bx > 0
            ? _countsLuma[mb * 16 + BlockIndexAt[by * 4 + bx - 1]]
            : _countsLuma[(mb - 1) * 16 + BlockIndexAt[by * 4 + 3]];
        var nB = !hasAbove ? 0 : by > 0
            ? _countsLuma[mb * 16 + BlockIndexAt[(by - 1) * 4 + bx]]
            : _countsLuma[(mb - _mbWidth) * 16 + BlockIndexAt[12 + bx]];
        return Nc(hasLeft, nA, hasAbove, nB);
    }

    private int ChromaNc(byte[] counts, int mbX, int mbY, int block)
    {
        var bx = block & 1;
        var by = block >> 1;
        var mb = mbY * _mbWidth + mbX;
        var hasLeft = bx > 0 || mbX > 0;
        var hasAbove = by > 0 || mbY > 0;
        var nA = !hasLeft ? 0 : bx > 0 ? counts[mb * 4 + by * 2] : counts[(mb - 1) * 4 + by * 2 + 1];
        var nB = !hasAbove ? 0 : by > 0 ? counts[mb * 4 + bx] : counts[(mb - _mbWidth) * 4 + 2 + bx];
        return Nc(hasLeft, nA, hasAbove, nB);
    }

    private static int Nc(bool hasLeft, int nA, bool hasAbove, int nB) =>
        hasLeft && hasAbove ? (nA + nB + 1) >> 1 : hasLeft ? nA : hasAbove ? nB : 0;

    // ------------------------------------------------------------------ picture access

    private static void Load(YuvPicture picture, int mbX, int mbY, byte[] luma, byte[] chroma)
    {
        for (var j = 0; j < 16; j++)
            picture.Y.AsSpan((mbY * 16 + j) * picture.PaddedWidth + mbX * 16, 16).CopyTo(luma.AsSpan(j * 16, 16));
        for (var j = 0; j < 8; j++)
        {
            var row = (mbY * 8 + j) * picture.ChromaWidth + mbX * 8;
            picture.Cb.AsSpan(row, 8).CopyTo(chroma.AsSpan(j * 8, 8));
            picture.Cr.AsSpan(row, 8).CopyTo(chroma.AsSpan(64 + j * 8, 8));
        }
    }

    private static void Store(YuvPicture picture, int mbX, int mbY, byte[] luma, byte[] chroma)
    {
        for (var j = 0; j < 16; j++)
            luma.AsSpan(j * 16, 16).CopyTo(picture.Y.AsSpan((mbY * 16 + j) * picture.PaddedWidth + mbX * 16, 16));
        for (var j = 0; j < 8; j++)
        {
            var row = (mbY * 8 + j) * picture.ChromaWidth + mbX * 8;
            chroma.AsSpan(j * 8, 8).CopyTo(picture.Cb.AsSpan(row, 8));
            chroma.AsSpan(64 + j * 8, 8).CopyTo(picture.Cr.AsSpan(row, 8));
        }
    }

    private static int[] Invert(int[] codeNumToValue)
    {
        var result = new int[codeNumToValue.Length];
        for (var codeNum = 0; codeNum < codeNumToValue.Length; codeNum++) result[codeNumToValue[codeNum]] = codeNum;
        return result;
    }
}
