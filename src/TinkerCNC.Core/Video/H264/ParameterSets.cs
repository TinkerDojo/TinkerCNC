namespace TinkerCNC.Video.H264;

/// <summary>What the encoder produces: Constrained Baseline profile, one slice per picture, a fixed quantizer.</summary>
/// <param name="Width">Visible width in pixels.</param>
/// <param name="Height">Visible height in pixels.</param>
/// <param name="Qp">Quantization parameter of P pictures (0 finest … 51 coarsest); I pictures use a slightly finer one.</param>
/// <param name="KeyframeInterval">Every how many pictures an IDR picture is coded.</param>
/// <param name="FramesPerSecond">The playback rate the level is chosen for.</param>
public sealed record H264Settings(int Width, int Height, int Qp = 26, int KeyframeInterval = 30, int FramesPerSecond = 30)
{
    public const int DefaultQp = 26;

    /// <summary>I pictures are quantized this much finer than P pictures, as the whole group refers to them.</summary>
    public const int IntraQpOffset = -2;

    public int IntraQp => Math.Max(0, Qp + IntraQpOffset);
}

/// <summary>The sequence and picture parameter sets of the encoder's stream (clauses 7.3.2.1 and 7.3.2.2).</summary>
internal static class ParameterSets
{
    public const int ProfileBaseline = 66;

    /// <summary>constraint_set0 and constraint_set1: the stream is also decodable by Main profile decoders.</summary>
    public const byte ProfileCompatibility = 0xC0;

    /// <summary>frame_num is written with this many bits (log2_max_frame_num_minus4 = 12).</summary>
    public const int FrameNumBits = 16;

    /// <summary>QP written in the picture parameter set; slices give their QP as a difference to it.</summary>
    public const int PicInitQp = 26;

    private sealed record Level(int Idc, int MaxFrameSizeMbs, int MaxMbsPerSecond);

    // Table A-1, the levels that matter for camera resolutions.
    private static readonly Level[] Levels =
    [
        new(30, 1620, 40500), new(31, 3600, 108000), new(32, 5120, 216000), new(40, 8192, 245760),
        new(42, 8704, 522240), new(50, 22080, 589824), new(51, 36864, 983040),
    ];

    public static int LevelFor(int macroblocks, int framesPerSecond)
    {
        var rate = (long)macroblocks * framesPerSecond;
        return (Levels.FirstOrDefault(l => l.MaxFrameSizeMbs >= macroblocks && l.MaxMbsPerSecond >= rate) ?? Levels[^1]).Idc;
    }

    /// <summary>The sequence parameter set as a NAL unit.</summary>
    public static byte[] Sps(H264Settings settings)
    {
        var mbWidth = (settings.Width + 15) / 16;
        var mbHeight = (settings.Height + 15) / 16;
        var cropRight = (mbWidth * 16 - settings.Width) / 2;
        var cropBottom = (mbHeight * 16 - settings.Height) / 2;

        var w = new BitWriter(64);
        w.Write(ProfileBaseline, 8);
        w.Write(ProfileCompatibility, 8);
        w.Write((uint)LevelFor(mbWidth * mbHeight, settings.FramesPerSecond), 8);
        w.WriteUe(0);                           // seq_parameter_set_id
        w.WriteUe(FrameNumBits - 4);            // log2_max_frame_num_minus4
        w.WriteUe(2);                           // pic_order_cnt_type: output order is decoding order
        w.WriteUe(1);                           // max_num_ref_frames
        w.WriteBit(0);                          // gaps_in_frame_num_value_allowed_flag
        w.WriteUe((uint)(mbWidth - 1));
        w.WriteUe((uint)(mbHeight - 1));
        w.WriteBit(1);                          // frame_mbs_only_flag
        w.WriteBit(1);                          // direct_8x8_inference_flag
        var cropped = cropRight > 0 || cropBottom > 0;
        w.WriteBit(cropped ? 1 : 0);
        if (cropped)
        {
            w.WriteUe(0);
            w.WriteUe((uint)cropRight);
            w.WriteUe(0);
            w.WriteUe((uint)cropBottom);
        }
        w.WriteBit(1);                          // vui_parameters_present_flag
        WriteVui(w);
        w.WriteTrailingBits();
        return NalUnit.Pack(NalUnit.HighPriority, NalUnit.SequenceParameterSet, w.ToArray());
    }

    /// <summary>Video usability information: BT.709 colour, and no picture reordering so players need not buffer.</summary>
    private static void WriteVui(BitWriter w)
    {
        w.WriteBit(0);                          // aspect_ratio_info_present_flag
        w.WriteBit(0);                          // overscan_info_present_flag
        w.WriteBit(1);                          // video_signal_type_present_flag
        w.Write(5, 3);                          // video_format: unspecified
        w.WriteBit(0);                          // video_full_range_flag
        w.WriteBit(1);                          // colour_description_present_flag
        w.Write(1, 8);                          // colour_primaries: BT.709
        w.Write(1, 8);                          // transfer_characteristics: BT.709
        w.Write(1, 8);                          // matrix_coefficients: BT.709
        w.WriteBit(0);                          // chroma_loc_info_present_flag
        w.WriteBit(0);                          // timing_info_present_flag
        w.WriteBit(0);                          // nal_hrd_parameters_present_flag
        w.WriteBit(0);                          // vcl_hrd_parameters_present_flag
        w.WriteBit(0);                          // pic_struct_present_flag
        w.WriteBit(1);                          // bitstream_restriction_flag
        w.WriteBit(1);                          // motion_vectors_over_pic_boundaries_flag
        w.WriteUe(0);                           // max_bytes_per_pic_denom
        w.WriteUe(0);                           // max_bits_per_mb_denom
        w.WriteUe(16);                          // log2_max_mv_length_horizontal
        w.WriteUe(16);                          // log2_max_mv_length_vertical
        w.WriteUe(0);                           // max_num_reorder_frames
        w.WriteUe(1);                           // max_dec_frame_buffering
    }

    /// <summary>The picture parameter set as a NAL unit: CAVLC, one reference, deblocking controlled by the slices.</summary>
    public static byte[] Pps()
    {
        var w = new BitWriter(16);
        w.WriteUe(0);                           // pic_parameter_set_id
        w.WriteUe(0);                           // seq_parameter_set_id
        w.WriteBit(0);                          // entropy_coding_mode_flag: CAVLC
        w.WriteBit(0);                          // bottom_field_pic_order_in_frame_present_flag
        w.WriteUe(0);                           // num_slice_groups_minus1
        w.WriteUe(0);                           // num_ref_idx_l0_default_active_minus1
        w.WriteUe(0);                           // num_ref_idx_l1_default_active_minus1
        w.WriteBit(0);                          // weighted_pred_flag
        w.Write(0, 2);                          // weighted_bipred_idc
        w.WriteSe(PicInitQp - 26);              // pic_init_qp_minus26
        w.WriteSe(0);                           // pic_init_qs_minus26
        w.WriteSe(0);                           // chroma_qp_index_offset
        w.WriteBit(1);                          // deblocking_filter_control_present_flag
        w.WriteBit(0);                          // constrained_intra_pred_flag
        w.WriteBit(0);                          // redundant_pic_cnt_present_flag
        w.WriteTrailingBits();
        return NalUnit.Pack(NalUnit.HighPriority, NalUnit.PictureParameterSet, w.ToArray());
    }
}
