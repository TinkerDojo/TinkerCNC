namespace TinkerCNC.Queue;

/// <summary>What has become of a <see cref="QueueEntry"/>.</summary>
public enum QueueEntryState
{
    /// <summary>Waiting for its turn (its remaining repeats).</summary>
    Pending,
    /// <summary>Its job is on the machine right now.</summary>
    Running,
    /// <summary>Its last job did not end well; it waits for a retry, a skip or removal.</summary>
    Failed,
    /// <summary>Every repeat ran; it is counted, not listed.</summary>
    Done,
}

/// <summary>
/// A job queued on one machine: a file run <see cref="Repeat"/> times, with the options of its setup or, for a job
/// started on the Job card, the options it was started with (<see cref="Options"/>). The file is either one of the
/// setup's files (<see cref="FileId"/>) or a copy queued for these runs only (<see cref="StoredFile"/> in the queued
/// files folder), which is removed once the entry is done.
/// </summary>
/// <param name="SetupId">The setup it belongs to; null for a job started from a file that is in no setup.</param>
/// <param name="Position">Order within the machine's queue.</param>
/// <param name="Done">How many of the repeats have run.</param>
/// <param name="RestartSegment">
/// Index of the tool operation the next run starts from, after a failure (null: the whole file).
/// </param>
/// <param name="Options">The entry's own start options; null: its setup's, as they are when it starts.</param>
public sealed record QueueEntry(
    long Id, string Machine, int Position, long? SetupId, long? FileId, string? StoredFile, string? OriginalFileName, string? Md5,
    int Repeat, int Done, QueueEntryState State, string? Error, int? RestartSegment, DateTime CreatedUtc,
    Jobs.JobStartOptions? Options = null)
{
    public bool IsQueuedCopy => FileId is null;

    /// <summary>Runs still to come.</summary>
    public int Remaining => Math.Max(0, Repeat - Done);
}
