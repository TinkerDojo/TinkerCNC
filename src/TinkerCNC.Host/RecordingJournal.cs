using TinkerCNC.Video.Mp4;

namespace TinkerCNC.Host;

/// <summary>
/// The sample list of a recording in progress, kept on disk next to the part file so the MP4 index can still be
/// written when the app did not get to finish the recording (see <see cref="RecordingStore.RecoverUnfinished"/>).
/// </summary>
public sealed class RecordingJournal : IDisposable
{
    public const string Suffix = ".idx";
    private static readonly byte[] Magic = "TCNCVID1"u8.ToArray();
    private const int EntrySize = sizeof(long) + sizeof(int) + sizeof(long) + sizeof(bool);

    private readonly BinaryWriter _writer;

    private RecordingJournal(string path, BinaryWriter writer)
    {
        Path = path;
        _writer = writer;
    }

    public string Path { get; }

    public static string PathFor(string partPath) => partPath + Suffix;

    /// <summary>Starts the journal of a recording: the track description, then one entry per sample.</summary>
    public static RecordingJournal Create(string partPath, Mp4VideoTrack track)
    {
        var path = PathFor(partPath);
        var writer = new BinaryWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        writer.Write(Magic);
        writer.Write(track.Width);
        writer.Write(track.Height);
        writer.Write(track.Timescale);
        writer.Write(track.Sps.Length);
        writer.Write(track.Sps);
        writer.Write(track.Pps.Length);
        writer.Write(track.Pps);
        writer.Flush();
        return new RecordingJournal(path, writer);
    }

    public void Append(Mp4Sample sample)
    {
        _writer.Write(sample.Offset);
        _writer.Write(sample.Size);
        _writer.Write(sample.Timestamp);
        _writer.Write(sample.IsSync);
        _writer.Flush();
    }

    public void Dispose() => _writer.Dispose();

    /// <summary>Reads a journal back; an entry cut short at the end is ignored.</summary>
    public static (Mp4VideoTrack Track, List<Mp4Sample> Samples) Read(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("Not a recording journal.");
        var width = reader.ReadInt32();
        var height = reader.ReadInt32();
        var timescale = reader.ReadInt32();
        var sps = reader.ReadBytes(reader.ReadInt32());
        var pps = reader.ReadBytes(reader.ReadInt32());
        var samples = new List<Mp4Sample>();
        while (reader.BaseStream.Length - reader.BaseStream.Position >= EntrySize)
            samples.Add(new Mp4Sample(reader.ReadInt64(), reader.ReadInt32(), reader.ReadInt64(), reader.ReadBoolean()));
        return (new Mp4VideoTrack(width, height, timescale, sps, pps), samples);
    }
}
