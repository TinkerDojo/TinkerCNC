using TinkerCNC.Jobs;

namespace TinkerCNC.Programs;

/// <summary>
/// A named set of start options (work origin, probing, job modes…) with notes, shared by all machines. Its G-code
/// files (<see cref="SetupFile"/>) run with these options: one setup, many files, as when the same job is cut with a
/// different file per piece.
/// </summary>
/// <param name="DeletedUtc">When it was deleted; a deleted setup keeps its files and can be restored.</param>
public sealed record Setup(
    long Id, string Name, string Notes, JobStartOptions Options, DateTime CreatedUtc, DateTime UpdatedUtc,
    DateTime? LastStartedUtc, int RunCount, DateTime? DeletedUtc)
{
    public bool IsDeleted => DeletedUtc is not null;
}

/// <summary>A G-code file of a <see cref="Setup"/>, with its own copy in the setup files folder.</summary>
/// <param name="StoredFile">The copy's file name in that folder (not a path, so the data folder can move).</param>
/// <param name="OriginalFileName">The name it had when it was added; it is uploaded to the machine under this name.</param>
/// <param name="DeletedUtc">When it was deleted; a deleted file keeps its copy and can be restored.</param>
public sealed record SetupFile(
    long Id, long SetupId, string StoredFile, string OriginalFileName, string Md5, DateTime CreatedUtc, DateTime UpdatedUtc,
    DateTime? LastStartedUtc, int RunCount, DateTime? DeletedUtc)
{
    public bool IsDeleted => DeletedUtc is not null;
}
