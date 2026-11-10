namespace TinkerCNC.Video.H264;

/// <summary>A coded picture: one NAL unit holding its slice.</summary>
public readonly record struct EncodedFrame(byte[] Nal, bool IsKeyframe);

/// <summary>
/// A compact H.264 encoder for camera recordings, without native code: Constrained Baseline profile, CAVLC, one
/// slice per picture, Intra_16x16 pictures at every <see cref="H264Settings.KeyframeInterval"/> and in between
/// P pictures that copy unchanged macroblocks from the previous picture and code the difference of the others.
/// There is no motion search and no deblocking filter; the fixed quantizer keeps the quality steady.
/// </summary>
public sealed class H264Encoder
{
    private readonly SliceCoder _coder;
    private readonly Quantizer _intraLuma;
    private readonly Quantizer _intraChroma;
    private readonly Quantizer _interLuma;
    private readonly Quantizer _interChroma;
    private readonly BitWriter _writer = new(1 << 16);
    private YuvPicture _reconstruction;
    private YuvPicture _reference;
    private int _frameIndex;
    private int _frameNum;
    private int _idrPicId;

    public H264Encoder(H264Settings settings)
    {
        if (settings.Width <= 0 || settings.Height <= 0) throw new ArgumentOutOfRangeException(nameof(settings), "The picture size must be positive.");
        if (settings.Qp is < 0 or > Quantizer.MaxQp) throw new ArgumentOutOfRangeException(nameof(settings), "QP must be between 0 and 51.");
        if (settings.KeyframeInterval < 1) throw new ArgumentOutOfRangeException(nameof(settings), "The keyframe interval must be at least 1.");
        Settings = settings;
        Sps = ParameterSets.Sps(settings);
        Pps = ParameterSets.Pps();
        _reconstruction = new YuvPicture(settings.Width, settings.Height);
        _reference = new YuvPicture(settings.Width, settings.Height);
        _coder = new SliceCoder(_reconstruction.MacroblocksWide, _reconstruction.MacroblocksHigh);
        _intraLuma = new Quantizer(settings.IntraQp);
        _intraChroma = new Quantizer(Quantizer.ChromaQpFor(settings.IntraQp));
        _interLuma = new Quantizer(settings.Qp);
        _interChroma = new Quantizer(Quantizer.ChromaQpFor(settings.Qp));
    }

    public H264Settings Settings { get; }

    /// <summary>The sequence parameter set, a NAL unit without start code.</summary>
    public byte[] Sps { get; }

    /// <summary>The picture parameter set, a NAL unit without start code.</summary>
    public byte[] Pps { get; }

    /// <summary>Pictures coded so far.</summary>
    public int FrameCount => _frameIndex;

    /// <summary>Codes the next picture, which must have the size of the settings.</summary>
    public EncodedFrame Encode(YuvPicture picture)
    {
        if (picture.Width != Settings.Width || picture.Height != Settings.Height)
            throw new ArgumentException("The picture does not have the encoder's size.", nameof(picture));

        var idr = _frameIndex % Settings.KeyframeInterval == 0;
        if (idr) _frameNum = 0;
        _writer.Reset();
        WriteSliceHeader(idr);
        _coder.Encode(_writer, picture, _reconstruction, idr ? null : _reference,
            idr ? _intraLuma : _interLuma, idr ? _intraChroma : _interChroma);
        _writer.WriteTrailingBits();
        var nal = NalUnit.Pack(idr ? NalUnit.HighPriority : 2, idr ? NalUnit.IdrSlice : NalUnit.Slice, _writer.ToArray());

        (_reference, _reconstruction) = (_reconstruction, _reference);
        _frameNum = (_frameNum + 1) % (1 << ParameterSets.FrameNumBits);
        _frameIndex++;
        if (idr) _idrPicId ^= 1;
        return new EncodedFrame(nal, idr);
    }

    private void WriteSliceHeader(bool idr)
    {
        var w = _writer;
        w.WriteUe(0);                                   // first_mb_in_slice
        w.WriteUe(idr ? 7u : 5u);                       // slice_type: all slices I, or all P
        w.WriteUe(0);                                   // pic_parameter_set_id
        w.Write((uint)_frameNum, ParameterSets.FrameNumBits);
        if (idr)
        {
            w.WriteUe((uint)_idrPicId);
        }
        else
        {
            w.WriteBit(0);                              // num_ref_idx_active_override_flag
            w.WriteBit(0);                              // ref_pic_list_modification_flag_l0
        }
        if (idr)
        {
            w.WriteBit(0);                              // no_output_of_prior_pics_flag
            w.WriteBit(0);                              // long_term_reference_flag
        }
        else
        {
            w.WriteBit(0);                              // adaptive_ref_pic_marking_mode_flag: sliding window
        }
        w.WriteSe((idr ? Settings.IntraQp : Settings.Qp) - ParameterSets.PicInitQp);
        w.WriteUe(1);                                   // disable_deblocking_filter_idc
    }
}
