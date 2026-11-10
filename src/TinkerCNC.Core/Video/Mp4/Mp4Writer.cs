using System.Buffers.Binary;

namespace TinkerCNC.Video.Mp4;

/// <summary>The one video track of a recording: picture size, clock rate and the H.264 parameter sets (NAL units).</summary>
public sealed record Mp4VideoTrack(int Width, int Height, int Timescale, byte[] Sps, byte[] Pps);

/// <summary>One coded picture in the file: where its bytes are, its clock position and whether it can be decoded alone.</summary>
public readonly record struct Mp4Sample(long Offset, int Size, long Timestamp, bool IsSync);

/// <summary>
/// Writes an MP4 file with one H.264 video track: the file type and the media data as samples arrive, the index
/// (moov) when the recording ends. Until then the file is not playable; <see cref="Resume"/> finishes a file
/// whose recording was cut off when the samples are known from elsewhere.
/// </summary>
public sealed class Mp4Writer
{
    private const int FtypSize = 32;
    private const int MdatHeaderSize = 16;
    private const uint MovieTimescale = 1000;
    private static readonly DateTime Epoch1904 = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Stream _output;
    private readonly Mp4VideoTrack _track;
    private readonly List<Mp4Sample> _samples;
    private long _lastTimestamp = -1;
    private bool _finished;

    /// <summary>Starts a new file on an empty stream.</summary>
    public Mp4Writer(Stream output, Mp4VideoTrack track)
    {
        if (output.Length != 0) throw new ArgumentException("A new MP4 file needs an empty stream.", nameof(output));
        _output = output;
        _track = track;
        _samples = new List<Mp4Sample>();
        WriteFileHeader();
    }

    private Mp4Writer(Stream output, Mp4VideoTrack track, List<Mp4Sample> samples)
    {
        _output = output;
        _track = track;
        _samples = samples;
        _lastTimestamp = samples.Count > 0 ? samples[^1].Timestamp : -1;
    }

    /// <summary>Where the media data starts in the file.</summary>
    public static long DataOffset => FtypSize + MdatHeaderSize;

    public IReadOnlyList<Mp4Sample> Samples => _samples;

    /// <summary>
    /// Takes over a file whose header and samples were written but whose index was not: the samples that fit in
    /// the file are kept, anything after the last complete one is dropped. <see cref="Finish"/> completes the file.
    /// </summary>
    public static Mp4Writer Resume(Stream output, Mp4VideoTrack track, IEnumerable<Mp4Sample> samples)
    {
        var complete = samples.TakeWhile(s => s.Offset + s.Size <= output.Length).ToList();
        var end = complete.Count > 0 ? complete[^1].Offset + complete[^1].Size : DataOffset;
        output.SetLength(end);
        output.Position = end;
        return new Mp4Writer(output, track, complete);
    }

    /// <summary>Appends one coded picture (a NAL unit without start code). Timestamps must increase; a repeated one is nudged forward.</summary>
    public Mp4Sample AddSample(ReadOnlySpan<byte> nal, long timestamp, bool isSync)
    {
        if (_finished) throw new InvalidOperationException("The file is finished.");
        if (timestamp <= _lastTimestamp) timestamp = _lastTimestamp + 1;
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)nal.Length);
        var sample = new Mp4Sample(_output.Position, nal.Length + 4, timestamp, isSync);
        _output.Write(length);
        _output.Write(nal);
        _samples.Add(sample);
        _lastTimestamp = timestamp;
        return sample;
    }

    /// <summary>Closes the media data and writes the index. The stream stays open.</summary>
    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        var dataEnd = _output.Position;
        Span<byte> size = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(size, (ulong)(dataEnd - FtypSize));
        _output.Position = FtypSize + 8;
        _output.Write(size);
        _output.Position = dataEnd;
        _output.Write(BuildMoov(_track, _samples));
        _output.Flush();
    }

    private void WriteFileHeader()
    {
        var w = new BoxWriter();
        w.Begin("ftyp");
        w.WriteType("isom");
        w.WriteUInt32(0x200);
        w.WriteType("isom");
        w.WriteType("iso2");
        w.WriteType("avc1");
        w.WriteType("mp41");
        w.End();
        // The media data box with a 64-bit size, filled in by Finish.
        w.WriteUInt32(1);
        w.WriteType("mdat");
        w.WriteUInt64(0);
        var header = w.ToArray();
        if (header.Length != DataOffset) throw new InvalidOperationException("Unexpected header size.");
        _output.Write(header);
    }

    /// <summary>The movie box: one video track whose samples are each their own chunk.</summary>
    public static byte[] BuildMoov(Mp4VideoTrack track, IReadOnlyList<Mp4Sample> samples)
    {
        var durations = Durations(samples);
        var trackDuration = (ulong)durations.Sum();
        var movieDuration = (uint)(trackDuration * MovieTimescale / (ulong)track.Timescale);
        var time = (uint)(DateTime.UtcNow - Epoch1904).TotalSeconds;

        var w = new BoxWriter();
        w.Begin("moov");

        w.BeginFull("mvhd", 0, 0);
        w.WriteUInt32(time);
        w.WriteUInt32(time);
        w.WriteUInt32(MovieTimescale);
        w.WriteUInt32(movieDuration);
        w.WriteUInt32(0x00010000);          // rate 1.0
        w.WriteUInt16(0x0100);              // volume 1.0
        w.WriteZeros(10);
        w.WriteIdentityMatrix();
        w.WriteZeros(24);
        w.WriteUInt32(2);                   // next_track_ID
        w.End();

        w.Begin("trak");
        w.BeginFull("tkhd", 0, 3);          // enabled, in movie
        w.WriteUInt32(time);
        w.WriteUInt32(time);
        w.WriteUInt32(1);                   // track_ID
        w.WriteUInt32(0);
        w.WriteUInt32(movieDuration);
        w.WriteZeros(8);
        w.WriteUInt16(0);                   // layer
        w.WriteUInt16(0);                   // alternate_group
        w.WriteUInt16(0);                   // volume (video)
        w.WriteUInt16(0);
        w.WriteIdentityMatrix();
        w.WriteFixed(track.Width);
        w.WriteFixed(track.Height);
        w.End();

        w.Begin("mdia");
        w.BeginFull("mdhd", 0, 0);
        w.WriteUInt32(time);
        w.WriteUInt32(time);
        w.WriteUInt32((uint)track.Timescale);
        w.WriteUInt32((uint)trackDuration);
        w.WriteUInt16(0x55C4);              // language "und"
        w.WriteUInt16(0);
        w.End();
        w.BeginFull("hdlr", 0, 0);
        w.WriteUInt32(0);
        w.WriteType("vide");
        w.WriteZeros(12);
        w.WriteBytes("VideoHandler\0"u8);
        w.End();

        w.Begin("minf");
        w.BeginFull("vmhd", 0, 1);
        w.WriteZeros(8);                    // graphics mode and opcolor
        w.End();
        w.Begin("dinf");
        w.BeginFull("dref", 0, 0);
        w.WriteUInt32(1);
        w.BeginFull("url ", 0, 1);          // the data is in this file
        w.End();
        w.End();
        w.End();

        w.Begin("stbl");
        WriteSampleDescription(w, track);
        WriteTimeToSample(w, durations);
        WriteSyncSamples(w, samples);
        w.BeginFull("stsc", 0, 0);
        w.WriteUInt32(1);
        w.WriteUInt32(1);                   // first_chunk
        w.WriteUInt32(1);                   // samples_per_chunk
        w.WriteUInt32(1);                   // sample_description_index
        w.End();
        w.BeginFull("stsz", 0, 0);
        w.WriteUInt32(0);
        w.WriteUInt32((uint)samples.Count);
        foreach (var s in samples) w.WriteUInt32((uint)s.Size);
        w.End();
        WriteChunkOffsets(w, samples);
        w.End();                            // stbl
        w.End();                            // minf
        w.End();                            // mdia
        w.End();                            // trak
        w.End();                            // moov
        return w.ToArray();
    }

    private static void WriteSampleDescription(BoxWriter w, Mp4VideoTrack track)
    {
        w.BeginFull("stsd", 0, 0);
        w.WriteUInt32(1);
        w.Begin("avc1");
        w.WriteZeros(6);
        w.WriteUInt16(1);                   // data_reference_index
        w.WriteZeros(16);
        w.WriteUInt16((ushort)track.Width);
        w.WriteUInt16((ushort)track.Height);
        w.WriteUInt32(0x00480000);          // 72 dpi
        w.WriteUInt32(0x00480000);
        w.WriteUInt32(0);
        w.WriteUInt16(1);                   // frame_count
        var name = "TinkerCNC"u8;
        w.WriteUInt8((byte)name.Length);
        w.WriteBytes(name);
        w.WriteZeros(31 - name.Length);
        w.WriteUInt16(0x0018);              // depth
        w.WriteInt16(-1);
        w.Begin("avcC");
        w.WriteUInt8(1);                    // configurationVersion
        w.WriteUInt8(track.Sps[1]);         // AVCProfileIndication
        w.WriteUInt8(track.Sps[2]);         // profile_compatibility
        w.WriteUInt8(track.Sps[3]);         // AVCLevelIndication
        w.WriteUInt8(0xFF);                 // 4-byte NAL lengths
        w.WriteUInt8(0xE1);                 // one sequence parameter set
        w.WriteUInt16((ushort)track.Sps.Length);
        w.WriteBytes(track.Sps);
        w.WriteUInt8(1);                    // one picture parameter set
        w.WriteUInt16((ushort)track.Pps.Length);
        w.WriteBytes(track.Pps);
        w.End();
        w.End();
        w.End();
    }

    private static void WriteTimeToSample(BoxWriter w, IReadOnlyList<long> durations)
    {
        var runs = new List<(uint Count, uint Delta)>();
        foreach (var d in durations)
        {
            if (runs.Count > 0 && runs[^1].Delta == (uint)d) runs[^1] = (runs[^1].Count + 1, (uint)d);
            else runs.Add((1, (uint)d));
        }
        w.BeginFull("stts", 0, 0);
        w.WriteUInt32((uint)runs.Count);
        foreach (var (count, delta) in runs)
        {
            w.WriteUInt32(count);
            w.WriteUInt32(delta);
        }
        w.End();
    }

    private static void WriteSyncSamples(BoxWriter w, IReadOnlyList<Mp4Sample> samples)
    {
        var sync = samples.Select((s, i) => (s.IsSync, Number: (uint)i + 1)).Where(x => x.IsSync).Select(x => x.Number).ToList();
        w.BeginFull("stss", 0, 0);
        w.WriteUInt32((uint)sync.Count);
        foreach (var n in sync) w.WriteUInt32(n);
        w.End();
    }

    private static void WriteChunkOffsets(BoxWriter w, IReadOnlyList<Mp4Sample> samples)
    {
        var wide = samples.Count > 0 && samples[^1].Offset > uint.MaxValue;
        w.BeginFull(wide ? "co64" : "stco", 0, 0);
        w.WriteUInt32((uint)samples.Count);
        foreach (var s in samples)
        {
            if (wide) w.WriteUInt64((ulong)s.Offset);
            else w.WriteUInt32((uint)s.Offset);
        }
        w.End();
    }

    /// <summary>How long each sample is shown: until the next one; the last one as long as the one before it.</summary>
    private static List<long> Durations(IReadOnlyList<Mp4Sample> samples)
    {
        var durations = new List<long>(samples.Count);
        for (var i = 0; i + 1 < samples.Count; i++) durations.Add(samples[i + 1].Timestamp - samples[i].Timestamp);
        if (samples.Count > 0) durations.Add(durations.Count > 0 ? durations[^1] : 1);
        return durations;
    }
}
