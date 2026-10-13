namespace Rockford.Tests;

/// <summary>
/// Finds the game files the data-backed tests need.
///
/// The test host runs with the working directory set to the test binary's output
/// folder, so a relative path from the README never resolves on its own. Both
/// environment variables therefore get tried against the repository root as well.
/// Unset means skip; set but wrong throws, because a silent skip once hid a wrong
/// command in the README for the life of the repository.
/// </summary>
public static class GameFiles
{
    /// <summary>A directory holding CELLMAPS.BIN, or null when ROCKFORD_DATA is unset.</summary>
    public static string? DataDirectory => Resolve(
        "ROCKFORD_DATA",
        candidate => File.Exists(Path.Combine(candidate, "CellMaps.BIN".ToUpperInvariant())),
        "no CELLMAPS.BIN was found there. Point it at one extracted game directory, "
        + "for example extracted/ega1.");

    /// <summary>
    /// A directory holding the November 1987 release's files, or null when
    /// ROCKFORD_DATA_1987 is unset.
    ///
    /// Separate from <see cref="DataDirectory"/> because the two releases are
    /// compared against each other: r871109 is the only surviving source of the
    /// scuba and player artwork, and its cave file is the twenty caves the 1988
    /// one keeps as its first twenty records.
    /// </summary>
    public static string? DataDirectory1987 => Resolve(
        "ROCKFORD_DATA_1987",
        candidate => File.Exists(Path.Combine(candidate, "SCUBA.FIL")),
        "no SCUBA.FIL was found there. Point it at the 1987 release's second disk, "
        + "extracted/ega1987-2.");

    /// <summary>The repository root, found by the solution file beside it.</summary>
    public static string? Root
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "Rockford.slnx")))
                    return d.FullName;
            return null;
        }
    }

    /// <summary>An unpacked executable, or null when ROCKFORD_EXE is unset.</summary>
    public static string? Executable => Resolve(
        "ROCKFORD_EXE",
        File.Exists,
        "there is no file there. Point it at an unpacked executable, for example "
        + "unpacked/vga.bin.");

    private static string? Resolve(string variable, Func<string, bool> usable, string complaint)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value)) return null;

        foreach (var candidate in Candidates(value))
            if (usable(candidate))
                return candidate;

        throw new FileNotFoundException(
            $"{variable} is set to '{value}' but {complaint} Unset it to skip these tests.");
    }

    private static IEnumerable<string> Candidates(string value)
    {
        yield return value;
        if (Path.IsPathRooted(value)) yield break;

        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (!File.Exists(Path.Combine(d.FullName, "Rockford.slnx"))) continue;
            yield return Path.Combine(d.FullName, value);
            yield break;
        }
    }
}
