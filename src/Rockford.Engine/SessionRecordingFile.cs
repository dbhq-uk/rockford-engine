using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rockford.Engine;

/// <summary>
/// A recording on disk.
/// </summary>
/// <remarks>
/// JSON, and indented, because the point of a recording is that somebody reads
/// it. A death that needs explaining is explained by looking at what was
/// actually pressed, and a format needing a tool to see at all puts a step
/// between the question and the answer.
///
/// The cost is size: a long session is tens of thousands of calls and this is
/// the verbose way to hold them. That is the right trade while the sessions
/// being kept are the ones somebody is investigating; it stops being the right
/// trade the moment anything records continuously, and that is the point at
/// which to change it rather than now.
/// </remarks>
public static class SessionRecordingFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The recording as text.</summary>
    public static string ToJson(SessionRecording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        return JsonSerializer.Serialize(Wire.From(recording), Options);
    }

    /// <summary>A recording read back, or null if the text is not one.</summary>
    public static SessionRecording? FromJson(string json)
    {
        var wire = JsonSerializer.Deserialize<Wire>(json, Options);
        return wire?.ToRecording();
    }

    public static void Save(SessionRecording recording, string path) =>
        File.WriteAllText(path, ToJson(recording));

    public static SessionRecording? Load(string path) =>
        FromJson(File.ReadAllText(path));

    /// <summary>
    /// The shape on disk, which is deliberately not the shape in memory.
    /// </summary>
    /// <remarks>
    /// A version number first, so a file from a later build says so rather than
    /// deserialising into something plausible and wrong - the failure mode a
    /// format without one always eventually has.
    ///
    /// The calls are three fields rather than an object each, which is most of
    /// the file. `[["Tick", 8, 0], ...]` would be smaller still and is not worth
    /// the loss of being able to read a line and know what it says.
    /// </remarks>
    private sealed record Wire(
        int Version,
        SessionIdentity Identity,
        List<Wire.Call> Calls)
    {
        public const int Current = 1;

        public sealed record Call(HostCallKind Kind, int A, int B);

        public static Wire From(SessionRecording recording) => new(
            Current,
            recording.Identity,
            [.. recording.Calls.Select(c => new Call(c.Kind, c.A, c.B))]);

        public SessionRecording ToRecording()
        {
            if (Version != Current)
            {
                throw new InvalidOperationException(
                    $"this recording is version {Version} and this build reads {Current}");
            }

            return new SessionRecording(
                Identity,
                [.. Calls.Select(c => new HostCall(c.Kind, c.A, c.B))]);
        }
    }
}
