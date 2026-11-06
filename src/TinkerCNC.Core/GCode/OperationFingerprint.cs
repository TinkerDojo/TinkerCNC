using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TinkerCNC.GCode;

/// <summary>
/// Identifies a tool operation by its G-code, independent of formatting: each line is reduced to its
/// address words (comments, <c>;@MKR</c> metadata and <c>N</c> line numbers removed, numbers re-formatted
/// invariantly, letters upper-cased), empty lines are dropped, and the result is hashed with SHA-256.
/// The same operation therefore matches across files, renames and reordered toolpaths.
/// </summary>
public static class OperationFingerprint
{
    public static string Compute(IEnumerable<string> lines)
    {
        var canonical = string.Join('\n', lines.Select(Canonical).Where(l => l.Length > 0));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string Canonical(string line) =>
        string.Join(' ', GCodeLine.Parse(line).Words
            .Where(w => w.Letter != 'N')
            .Select(w => w.Letter + w.Value.ToString("0.#####", CultureInfo.InvariantCulture)));
}
