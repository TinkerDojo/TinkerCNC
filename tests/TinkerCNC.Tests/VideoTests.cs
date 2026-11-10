using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TinkerCNC.Host;
using TinkerCNC.Video;
using TinkerCNC.Video.H264;
using TinkerCNC.Video.Mp4;

namespace TinkerCNC.Tests;

public class BitWriterTests
{
    private static string Bits(Action<BitWriter> write)
    {
        var w = new BitWriter();
        write(w);
        var count = (int)w.BitCount;
        while (!w.IsByteAligned) w.WriteBit(0);
        var bytes = w.ToArray();
        var text = string.Concat(bytes.Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));
        return text[..count];
    }

    [Theory]
    [InlineData(0u, "1")]
    [InlineData(1u, "010")]
    [InlineData(2u, "011")]
    [InlineData(3u, "00100")]
    [InlineData(8u, "0001001")]
    public void UnsignedExpGolomb(uint value, string expected) => Assert.Equal(expected, Bits(w => w.WriteUe(value)));

    [Theory]
    [InlineData(0, "1")]
    [InlineData(1, "010")]
    [InlineData(-1, "011")]
    [InlineData(2, "00100")]
    [InlineData(-2, "00101")]
    public void SignedExpGolomb(int value, string expected) => Assert.Equal(expected, Bits(w => w.WriteSe(value)));

    [Fact]
    public void TrailingBitsAlignToAByte()
    {
        var w = new BitWriter();
        w.Write(0b101, 3);
        w.WriteTrailingBits();
        Assert.Equal(new byte[] { 0b1011_0000 }, w.ToArray());
    }

    [Fact]
    public void EmulationPreventionBreaksUpStartCodes()
    {
        var packed = NalUnit.Pack(3, NalUnit.SequenceParameterSet, [0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0xAB]);
        Assert.Equal(0x67, packed[0]);
        // Every "00 00" followed by a byte up to 03 gets a 03 in between; a 03 after a single 00 does not.
        Assert.Equal(new byte[] { 0x00, 0x00, 0x03, 0x01, 0x00, 0x00, 0x03, 0x00, 0x00, 0x03, 0x00, 0x03, 0xAB }, packed[1..]);
    }
}

public class CavlcTests
{
    /// <summary>The worked example from the literature: a 4×4 block with five coefficients, three trailing ones, nC = 0.</summary>
    [Fact]
    public void CodesTheTextbookBlock()
    {
        int[] coefficients = [0, 3, 0, 1, -1, -1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0];
        var w = new BitWriter();
        var total = CavlcWriter.WriteBlock(w, coefficients, nC: 0);
        var count = (int)w.BitCount;
        w.WriteTrailingBits();
        var bits = string.Concat(w.ToArray().Select(b => Convert.ToString(b, 2).PadLeft(8, '0')))[..count];
        Assert.Equal(5, total);
        Assert.Equal("000010001110010111101101", bits);
    }

    [Fact]
    public void EmptyBlockIsOneBitForLowNc()
    {
        var w = new BitWriter();
        Assert.Equal(0, CavlcWriter.WriteBlock(w, new int[16], nC: 0));
        Assert.Equal(1, w.BitCount);
    }

    [Fact]
    public void FixedLengthTableForHighNc()
    {
        var w = new BitWriter();
        CavlcWriter.WriteBlock(w, new int[16], nC: 8);
        Assert.Equal(6, w.BitCount);
    }
}

public class TransformTests
{
    [Fact]
    public void ForwardQuantizeScaleInverseReproducesTheResidual()
    {
        var q = new Quantizer(10);
        var rng = new Random(3);
        for (var trial = 0; trial < 50; trial++)
        {
            var residual = new int[16];
            for (var i = 0; i < 16; i++) residual[i] = rng.Next(-60, 61);
            var coefficients = new int[16];
            Transform.Forward4x4(residual, coefficients);
            var block = new int[16];
            for (var i = 0; i < 16; i++) block[i] = q.Scaled(q.Quantize(coefficients[i], i, q.InterRounding), i);
            Transform.Inverse4x4(block);
            for (var i = 0; i < 16; i++) Assert.InRange(block[i], residual[i] - 3, residual[i] + 3);
        }
    }

    [Fact]
    public void FlatBlockSurvivesTheLumaDcPath()
    {
        // A flat 16×16 residual of 10: every block's DC is 160, the Hadamard puts 2560 into one coefficient.
        var q = new Quantizer(26);
        var dc = new int[16];
        Array.Fill(dc, 160);
        Transform.Hadamard4x4(dc);
        Assert.Equal(2560, dc[0]);
        for (var i = 0; i < 16; i++) dc[i] = q.QuantizeLumaDc(dc[i], q.IntraRounding);
        Transform.Hadamard4x4(dc);
        var block = new int[16];
        block[0] = q.ScaledLumaDc(dc[5]);
        Transform.Inverse4x4(block);
        Assert.All(block, v => Assert.InRange(v, 9, 11));
    }

    [Fact]
    public void FlatChromaSurvivesTheChromaDcPath()
    {
        var q = new Quantizer(Quantizer.ChromaQpFor(26));
        var dc = new[] { 160, 160, 160, 160 };
        Transform.Hadamard2x2(dc);
        for (var i = 0; i < 4; i++) dc[i] = q.QuantizeChromaDc(dc[i], q.IntraRounding);
        Transform.Hadamard2x2(dc);
        var block = new int[16];
        block[0] = q.ScaledChromaDc(dc[3]);
        Transform.Inverse4x4(block);
        Assert.All(block, v => Assert.InRange(v, 9, 11));
    }

    [Fact]
    public void ChromaQpFollowsTheTable()
    {
        Assert.Equal(26, Quantizer.ChromaQpFor(26));
        Assert.Equal(29, Quantizer.ChromaQpFor(30));
        Assert.Equal(39, Quantizer.ChromaQpFor(51));
    }
}

public class ColorConversionTests
{
    private static YuvPicture Convert(byte r, byte g, byte b, int width = 4, int height = 2)
    {
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            rgba[i * 4] = r;
            rgba[i * 4 + 1] = g;
            rgba[i * 4 + 2] = b;
            rgba[i * 4 + 3] = 255;
        }
        var picture = new YuvPicture(width, height);
        ColorConversion.RgbaToYuv420(rgba, width * 4, picture);
        return picture;
    }

    [Fact]
    public void WhiteAndBlackMapToLimitedRange()
    {
        Assert.Equal(235, Convert(255, 255, 255).Y[0]);
        Assert.Equal(16, Convert(0, 0, 0).Y[0]);
        Assert.Equal(128, Convert(128, 128, 128).Cb[0]);
        Assert.Equal(128, Convert(128, 128, 128).Cr[0]);
    }

    [Fact]
    public void RedRaisesCrAndBlueRaisesCb()
    {
        Assert.True(Convert(255, 0, 0).Cr[0] > 200);
        Assert.True(Convert(0, 0, 255).Cb[0] > 200);
    }

    [Fact]
    public void PaddingIsFilledFromTheEdge()
    {
        var picture = Convert(200, 200, 200, width: 3, height: 3);
        Assert.Equal(16, picture.PaddedWidth);
        Assert.Equal(picture.Y[2], picture.Y[15]);
        Assert.Equal(picture.Y[2 * 16], picture.Y[15 * 16 + 15]);
    }
}

public static class TestPictures
{
    /// <summary>A gradient with a square that moves with the frame number and a little noise.</summary>
    public static YuvPicture Synthetic(int width, int height, int frame, int seed = 1)
    {
        var rng = new Random(seed + frame);
        var picture = new YuvPicture(width, height);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var v = 40 + x * 150 / width + y * 40 / height + rng.Next(-2, 3);
                var sx = frame * 3 % Math.Max(1, width - 16);
                if (x >= sx && x < sx + 16 && y >= 8 && y < 24) v = 220;
                picture.Y[y * picture.PaddedWidth + x] = (byte)Math.Clamp(v, 16, 235);
            }
        for (var y = 0; y < (height + 1) / 2; y++)
            for (var x = 0; x < (width + 1) / 2; x++)
            {
                picture.Cb[y * picture.ChromaWidth + x] = (byte)(100 + x * 60 / picture.ChromaWidth);
                picture.Cr[y * picture.ChromaWidth + x] = (byte)(120 + y * 50 / picture.ChromaHeight);
            }
        picture.ReplicateEdges();
        return picture;
    }

    /// <summary>The visible samples as planar yuv420p, the layout decoders emit.</summary>
    public static byte[] Raw(YuvPicture p)
    {
        var cw = (p.Width + 1) / 2;
        var ch = (p.Height + 1) / 2;
        using var m = new MemoryStream();
        for (var y = 0; y < p.Height; y++) m.Write(p.Y, y * p.PaddedWidth, p.Width);
        for (var y = 0; y < ch; y++) m.Write(p.Cb, y * p.ChromaWidth, cw);
        for (var y = 0; y < ch; y++) m.Write(p.Cr, y * p.ChromaWidth, cw);
        return m.ToArray();
    }
}

public class H264EncoderTests
{
    [Fact]
    public void ParameterSetsDescribeTheStream()
    {
        var encoder = new H264Encoder(new H264Settings(100, 70));
        Assert.Equal(0x67, encoder.Sps[0]);
        Assert.Equal(66, encoder.Sps[1]);          // Baseline
        Assert.Equal(0xC0, encoder.Sps[2]);        // constraint_set0 and 1
        Assert.Equal(0x68, encoder.Pps[0]);
    }

    [Fact]
    public void KeyframesComeAtTheInterval()
    {
        var encoder = new H264Encoder(new H264Settings(64, 48, KeyframeInterval: 4));
        var keyframes = Enumerable.Range(0, 9).Select(f => encoder.Encode(TestPictures.Synthetic(64, 48, f)).IsKeyframe).ToList();
        Assert.Equal([true, false, false, false, true, false, false, false, true], keyframes);
    }

    [Fact]
    public void IdrSlicesAreMarkedAsSuch()
    {
        var encoder = new H264Encoder(new H264Settings(64, 48));
        var first = encoder.Encode(TestPictures.Synthetic(64, 48, 0));
        var second = encoder.Encode(TestPictures.Synthetic(64, 48, 1));
        Assert.Equal(NalUnit.IdrSlice, first.Nal[0] & 0x1F);
        Assert.Equal(NalUnit.Slice, second.Nal[0] & 0x1F);
        Assert.True(second.Nal.Length < first.Nal.Length, "a P picture of a nearly unchanged scene is smaller than the I picture");
    }

    [Fact]
    public void UnchangedPictureCodesToAlmostNothing()
    {
        var encoder = new H264Encoder(new H264Settings(160, 96));
        var picture = TestPictures.Synthetic(160, 96, 0);
        encoder.Encode(picture);
        var repeat = encoder.Encode(picture);
        Assert.True(repeat.Nal.Length < 16, $"an identical picture is all skipped macroblocks, but took {repeat.Nal.Length} bytes");
    }

    [Fact]
    public void RefusesAnotherPictureSize()
    {
        var encoder = new H264Encoder(new H264Settings(64, 48));
        Assert.Throws<ArgumentException>(() => encoder.Encode(TestPictures.Synthetic(48, 48, 0)));
    }

    [Fact]
    public void LevelFollowsThePictureSize()
    {
        Assert.Equal(30, ParameterSets.LevelFor(40 * 23, 30));      // 640×360
        Assert.Equal(31, ParameterSets.LevelFor(80 * 45, 30));      // 1280×720
        Assert.Equal(40, ParameterSets.LevelFor(120 * 68, 30));     // 1920×1080
    }
}

/// <summary>A minimal reader of the box structure, enough to check what the muxer wrote.</summary>
public static class Mp4Boxes
{
    public sealed record Box(string Type, long Offset, long Size, long HeaderSize);

    public static List<Box> Children(byte[] data, long start, long end, int depth = 0)
    {
        var boxes = new List<Box>();
        var position = start;
        while (position + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)position, 4));
            var type = Encoding.ASCII.GetString(data, (int)position + 4, 4);
            var header = 8L;
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan((int)position + 8, 8));
                header = 16;
            }
            if (size < header) throw new InvalidDataException($"box {type} at {position} has size {size}");
            boxes.Add(new Box(type, position, size, header));
            position += size;
        }
        return boxes;
    }

    private static readonly HashSet<string> Containers = ["moov", "trak", "mdia", "minf", "stbl", "dinf"];

    public static Box? Find(byte[] data, string path)
    {
        var start = 0L;
        long end = data.Length;
        Box? found = null;
        foreach (var type in path.Split('/'))
        {
            found = Children(data, start, end).FirstOrDefault(b => b.Type == type);
            if (found is null) return null;
            start = found.Offset + found.HeaderSize;
            end = found.Offset + found.Size;
            if (type == "stsd") start += 8;   // version/flags and entry_count before the sample entry
            if (type == "avc1") start += 78;  // the visual sample entry's fields before its child boxes
        }
        return found;
    }

    public static uint EntryCount(byte[] data, string path)
    {
        var box = Find(data, path) ?? throw new InvalidOperationException(path + " not found");
        return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)(box.Offset + box.HeaderSize + 4), 4));
    }

    /// <summary>sample_count of the sample size box (after version/flags and the constant sample_size).</summary>
    public static uint SampleCount(byte[] data)
    {
        var box = Find(data, "moov/trak/mdia/minf/stbl/stsz") ?? throw new InvalidOperationException("stsz not found");
        return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)(box.Offset + box.HeaderSize + 8), 4));
    }
}

public class Mp4WriterTests
{
    private static (byte[] File, int Frames, int Keyframes) Record(int frames, int keyframeInterval = 4)
    {
        var encoder = new H264Encoder(new H264Settings(64, 48, KeyframeInterval: keyframeInterval));
        using var stream = new MemoryStream();
        var writer = new Mp4Writer(stream, new Mp4VideoTrack(64, 48, 30, encoder.Sps, encoder.Pps));
        var keyframes = 0;
        for (var f = 0; f < frames; f++)
        {
            var coded = encoder.Encode(TestPictures.Synthetic(64, 48, f));
            if (coded.IsKeyframe) keyframes++;
            writer.AddSample(coded.Nal, f, coded.IsKeyframe);
        }
        writer.Finish();
        return (stream.ToArray(), frames, keyframes);
    }

    [Fact]
    public void FileHasTheThreeTopLevelBoxes()
    {
        var (file, _, _) = Record(6);
        var top = Mp4Boxes.Children(file, 0, file.Length).Select(b => b.Type).ToList();
        Assert.Equal(["ftyp", "mdat", "moov"], top);
        var mdat = Mp4Boxes.Children(file, 0, file.Length)[1];
        Assert.Equal(16, mdat.HeaderSize);
        Assert.Equal(Mp4Writer.DataOffset, mdat.Offset + mdat.HeaderSize);
    }

    [Fact]
    public void SampleTablesCountEveryPicture()
    {
        var (file, frames, keyframes) = Record(10, keyframeInterval: 4);
        Assert.Equal((uint)frames, Mp4Boxes.SampleCount(file));
        Assert.Equal((uint)keyframes, Mp4Boxes.EntryCount(file, "moov/trak/mdia/minf/stbl/stss"));
        Assert.Equal((uint)frames, Mp4Boxes.EntryCount(file, "moov/trak/mdia/minf/stbl/stco"));
        Assert.Equal(1u, Mp4Boxes.EntryCount(file, "moov/trak/mdia/minf/stbl/stts"));   // one run: every frame one tick
        Assert.NotNull(Mp4Boxes.Find(file, "moov/trak/mdia/minf/stbl/stsd/avc1/avcC"));
    }

    [Fact]
    public void RepeatedTimestampsAreNudgedForward()
    {
        var encoder = new H264Encoder(new H264Settings(32, 32));
        using var stream = new MemoryStream();
        var writer = new Mp4Writer(stream, new Mp4VideoTrack(32, 32, 1000, encoder.Sps, encoder.Pps));
        var a = writer.AddSample(encoder.Encode(TestPictures.Synthetic(32, 32, 0)).Nal, 100, true);
        var b = writer.AddSample(encoder.Encode(TestPictures.Synthetic(32, 32, 1)).Nal, 100, false);
        Assert.Equal(100, a.Timestamp);
        Assert.Equal(101, b.Timestamp);
    }

    [Fact]
    public void ResumeCompletesATruncatedFile()
    {
        var encoder = new H264Encoder(new H264Settings(64, 48));
        var track = new Mp4VideoTrack(64, 48, 30, encoder.Sps, encoder.Pps);
        using var stream = new MemoryStream();
        var writer = new Mp4Writer(stream, track);
        var samples = new List<Mp4Sample>();
        for (var f = 0; f < 5; f++)
        {
            var coded = encoder.Encode(TestPictures.Synthetic(64, 48, f));
            samples.Add(writer.AddSample(coded.Nal, f, coded.IsKeyframe));
        }
        // The app died: no index, and the last sample only half written.
        var cut = (int)(samples[^1].Offset + samples[^1].Size / 2);
        var truncated = new MemoryStream();
        truncated.Write(stream.ToArray(), 0, cut);

        var resumed = Mp4Writer.Resume(truncated, track, samples);
        Assert.Equal(4, resumed.Samples.Count);
        resumed.Finish();

        var file = truncated.ToArray();
        Assert.Equal(["ftyp", "mdat", "moov"], Mp4Boxes.Children(file, 0, file.Length).Select(b => b.Type).ToList());
        Assert.Equal(4u, Mp4Boxes.EntryCount(file, "moov/trak/mdia/minf/stbl/stco"));
    }
}

/// <summary>Stands in for the camera's JPEG frames: the "frame" bytes name the picture to produce.</summary>
public sealed class SyntheticFrames : IFrameDecoder
{
    public static byte[] Frame(int index, int width = 64, int height = 48) =>
        BitConverter.GetBytes(index).Concat(BitConverter.GetBytes(width)).Concat(BitConverter.GetBytes(height)).ToArray();

    public YuvPicture? Decode(byte[] frame) =>
        frame.Length == 12
            ? TestPictures.Synthetic(BitConverter.ToInt32(frame, 4), BitConverter.ToInt32(frame, 8), BitConverter.ToInt32(frame, 0))
            : null;
}

public sealed class VideoRecorderTests : IDisposable
{
    private readonly TempDataFolder _data = new();

    public void Dispose() => _data.Dispose();

    private static readonly RecordingPlan AllFrames = RecordingPlan.RealTimeKeeping(1);

    private RecordingStore Store() => new(_data.Paths, new SyntheticFrames(), NullLogger<RecordingStore>.Instance);

    [Fact]
    public async Task RecordsFramesIntoAnMp4()
    {
        var recorder = Store().CreateRecording("test", AllFrames);
        var start = DateTime.UtcNow;
        for (var f = 0; f < 6; f++) Assert.True(recorder.Add(SyntheticFrames.Frame(f), start.AddMilliseconds(f * 100)));
        var path = await recorder.FinishAsync();

        Assert.NotNull(path);
        Assert.EndsWith(".mp4", path);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(recorder.PartPath));
        Assert.False(File.Exists(RecordingJournal.PathFor(recorder.PartPath)));
        Assert.Equal(6, recorder.FramesWritten);
        var file = await File.ReadAllBytesAsync(path);
        Assert.Equal(6u, Mp4Boxes.EntryCount(file, "moov/trak/mdia/minf/stbl/stco"));
    }

    [Fact]
    public async Task RealTimeRecordingKeepsEveryFifthFrameAtItsCaptureTime()
    {
        var recorder = Store().CreateRecording("test", RecordingPlan.RealTime);
        var start = DateTime.UtcNow;
        var queued = Enumerable.Range(0, 20).Count(f => recorder.Add(SyntheticFrames.Frame(f), start.AddMilliseconds(f * 100)));
        var path = await recorder.FinishAsync();

        Assert.Equal(4, queued);
        Assert.Equal(4, recorder.FramesWritten);
        Assert.Equal(0, recorder.FramesDropped);
        var file = await File.ReadAllBytesAsync(path!);
        Assert.Equal(4u, Mp4Boxes.EntryCount(file, "moov/trak/mdia/minf/stbl/stco"));
        // One run of samples 500 ms apart: the frames keep the times they were captured at.
        Assert.Equal(1u, Mp4Boxes.EntryCount(file, "moov/trak/mdia/minf/stbl/stts"));
    }

    [Fact]
    public async Task PartFileIsReopenedWhileRecordingAndStaysWhole()
    {
        var recorder = Store().CreateRecording("test", AllFrames);
        var start = DateTime.UtcNow;
        const int frames = 25;                  // 6 s at 4 frames a second: reopened twice
        for (var f = 0; f < frames; f++)
        {
            Assert.True(recorder.Add(SyntheticFrames.Frame(f), start.AddMilliseconds(f * 250)));
            await WaitAsync(() => recorder.FramesWritten == f + 1);
        }
        var path = await recorder.FinishAsync();

        Assert.Equal(frames, recorder.FramesWritten);
        var file = await File.ReadAllBytesAsync(path!);
        Assert.Equal((uint)frames, Mp4Boxes.EntryCount(file, "moov/trak/mdia/minf/stbl/stco"));
    }

    [Fact]
    public async Task UndecodableFramesAreLeftOutAndNothingRecordedMeansNoFile()
    {
        var recorder = Store().CreateRecording("test", AllFrames);
        recorder.Add([1, 2, 3], DateTime.UtcNow);
        Assert.Null(await recorder.FinishAsync());
        Assert.False(File.Exists(recorder.Path));
    }

    [Fact]
    public async Task FramesOfAnotherSizeAreLeftOut()
    {
        var recorder = Store().CreateRecording("test", RecordingPlan.Timelapse(TimeSpan.FromSeconds(1), 30));
        var now = DateTime.UtcNow;
        recorder.Add(SyntheticFrames.Frame(0), now);
        recorder.Add(SyntheticFrames.Frame(1, 32, 32), now.AddSeconds(1));
        recorder.Add(SyntheticFrames.Frame(2), now.AddSeconds(2));
        await recorder.FinishAsync();
        Assert.Equal(2, recorder.FramesWritten);
    }

    [Fact]
    public async Task UnfinishedRecordingIsCompletedAtTheNextStart()
    {
        var store = Store();
        var recorder = store.CreateRecording("crash", AllFrames);
        var now = DateTime.UtcNow;
        for (var f = 0; f < 4; f++) recorder.Add(SyntheticFrames.Frame(f), now.AddMilliseconds(f * 100));
        // Wait for the frames to be written, then forget the recorder as a crash would.
        await WaitAsync(() => recorder.FramesWritten == 4);
        Assert.True(File.Exists(recorder.PartPath));
        Assert.True(File.Exists(RecordingJournal.PathFor(recorder.PartPath)));

        // A crashed app leaves the part file and the journal behind; here they are copied aside at that state,
        // since this recorder still holds them open and goes on to finish properly.
        var copyPart = Path.Combine(_data.Paths.Recordings, "copy.mp4.part");
        File.Copy(recorder.PartPath, copyPart);
        File.Copy(RecordingJournal.PathFor(recorder.PartPath), RecordingJournal.PathFor(copyPart));
        await recorder.FinishAsync();

        Assert.Equal(1, store.RecoverUnfinished());
        var recovered = Path.Combine(_data.Paths.Recordings, "copy.mp4");
        Assert.True(File.Exists(recovered));
        Assert.False(File.Exists(copyPart));
        Assert.False(File.Exists(RecordingJournal.PathFor(copyPart)));
        var file = await File.ReadAllBytesAsync(recovered);
        Assert.Equal(4u, Mp4Boxes.EntryCount(file, "moov/trak/mdia/minf/stbl/stco"));
        Assert.Equal(0, store.RecoverUnfinished());
    }

    [Fact]
    public void JournalRoundTrips()
    {
        var track = new Mp4VideoTrack(64, 48, 30, [0x67, 1, 2], [0x68, 3]);
        var part = Path.Combine(_data.Root, "j.mp4.part");
        using (var journal = RecordingJournal.Create(part, track))
        {
            journal.Append(new Mp4Sample(48, 100, 0, true));
            journal.Append(new Mp4Sample(148, 20, 1, false));
        }
        var (readTrack, samples) = RecordingJournal.Read(RecordingJournal.PathFor(part));
        Assert.Equal(track, readTrack with { Sps = track.Sps, Pps = track.Pps });
        Assert.Equal(track.Sps, readTrack.Sps);
        Assert.Equal([new Mp4Sample(48, 100, 0, true), new Mp4Sample(148, 20, 1, false)], samples);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }
}

public class FrameSelectionTests
{
    [Fact]
    public void EveryNthKeepsTheFirstFrameAndEveryNthAfterIt()
    {
        var selection = new EveryNthFrame(5);
        var kept = Enumerable.Range(0, 12).Where(i => selection.Keeps(i, null)).ToArray();
        Assert.Equal([0, 5, 10], kept);
    }

    [Fact]
    public void FixedIntervalKeepsTheFirstFrameAndThenOnePerInterval()
    {
        var selection = new FixedInterval(TimeSpan.FromSeconds(1));
        Assert.True(selection.Keeps(0, null));
        Assert.False(selection.Keeps(1, TimeSpan.FromMilliseconds(999)));
        Assert.True(selection.Keeps(2, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void RealTimeRecordingsKeepAFifthOfTheFrames()
    {
        Assert.False(RecordingPlan.RealTime.IsTimelapse);
        Assert.Equal(5, Assert.IsType<EveryNthFrame>(RecordingPlan.RealTime.Selection).Every);
    }
}

public class FrameTimingTests
{
    [Fact]
    public void RealTimeUsesMilliseconds()
    {
        var timing = new RealTimeTiming();
        Assert.Equal(1000, timing.Timescale);
        Assert.Equal(2500, timing.TimestampOf(7, TimeSpan.FromSeconds(2.5)));
    }

    [Fact]
    public void FixedRateCountsFrames()
    {
        var plan = RecordingPlan.Timelapse(TimeSpan.FromSeconds(1), 30);
        Assert.True(plan.IsTimelapse);
        Assert.Equal(30, plan.Timing.Timescale);
        Assert.Equal(7, plan.Timing.TimestampOf(7, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void JobTimelapseIsOneFrameASecondAt30Fps()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), JobTimelapse.Interval);
        Assert.Equal(30, JobTimelapse.PlaybackFramesPerSecond);
        Assert.Equal(30, JobTimelapse.SpeedUp);
    }
}

/// <summary>Decodes a recording with ffmpeg when one is installed (CI runners have it) and compares it to the source.</summary>
public class FfmpegTests
{
    private static string? FindFfmpeg()
    {
        if (Environment.GetEnvironmentVariable("TINKERCNC_FFMPEG") is { Length: > 0 } configured && File.Exists(configured)) return configured;
        var names = OperatingSystem.IsWindows() ? new[] { "ffmpeg.exe" } : new[] { "ffmpeg" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    [Fact]
    public void DecodesWithFfmpegWhenInstalled()
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;

        const int width = 96, height = 72, frames = 40;
        var encoder = new H264Encoder(new H264Settings(width, height, KeyframeInterval: 15));
        var file = Path.Combine(Path.GetTempPath(), "tinkercnc-ffmpeg-" + Guid.NewGuid().ToString("N") + ".mp4");
        var sources = new List<byte[]>();
        try
        {
            using (var stream = File.Create(file))
            {
                var writer = new Mp4Writer(stream, new Mp4VideoTrack(width, height, 30, encoder.Sps, encoder.Pps));
                for (var f = 0; f < frames; f++)
                {
                    var picture = TestPictures.Synthetic(width, height, f);
                    sources.Add(TestPictures.Raw(picture));
                    var coded = encoder.Encode(picture);
                    writer.AddSample(coded.Nal, f, coded.IsKeyframe);
                }
                writer.Finish();
            }

            var info = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { "-nostdin", "-v", "error", "-i", file, "-f", "rawvideo", "-pix_fmt", "yuv420p", "-" }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            var errors = process.StandardError.ReadToEndAsync();
            using var decoded = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(decoded);
            process.WaitForExit();

            Assert.Equal("", errors.Result.Trim());
            var frameSize = sources[0].Length;
            Assert.Equal(frames * frameSize, decoded.Length);
            var output = decoded.ToArray();
            for (var f = 0; f < frames; f++)
            {
                var psnr = Psnr(sources[f], output.AsSpan(f * frameSize, width * height), width * height);
                Assert.True(psnr > 36, $"frame {f}: luma PSNR {psnr:0.0} dB");
            }
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static double Psnr(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int count)
    {
        double sum = 0;
        for (var i = 0; i < count; i++)
        {
            var d = a[i] - b[i];
            sum += d * d;
        }
        var mse = sum / count;
        return mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }
}
