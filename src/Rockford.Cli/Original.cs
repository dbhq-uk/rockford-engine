using Rockford.Reference;

namespace Rockford.Cli;

/// <summary>
/// Boots the original in the interpreter and says what it did.
/// </summary>
/// <remarks>
/// The probe, not the comparison. It exists so that "how far does it get" has a
/// one-line answer during the work, and so that a new fault reports the address
/// it happened at rather than a stack trace.
/// </remarks>
public static class Original
{
    public static int Run(string[] volume, long limit, int? stopAt, string keys, string? dump, string? scancodes)
    {
        foreach (var directory in volume)
        {
            if (Directory.Exists(directory)) continue;
            Console.Error.WriteLine($"No directory at {directory}.");
            return 2;
        }

        var machine = Harness.BootFrom(volume);
        Console.WriteLine($"loaded  cs:ip {machine.Machine.Segments[Machine8086.CS]:X4}:"
                          + $"{machine.Machine.Ip:X4}   "
                          + $"ss:sp {machine.Machine.Segments[Machine8086.SS]:X4}:"
                          + $"{machine.Machine.Registers[Machine8086.SP]:X4}   "
                          + $"{machine.Machine.LoadParagraphs:X4} paragraphs");

        machine.Dos.Type(keys);

        if (Environment.GetEnvironmentVariable("ROCKFORD_WATCH") is { } watch)
        {
            machine.Machine.Watch = Convert.ToInt32(watch, 16);
        }

        var histogram = new Dictionary<int, long>();
        // "1C@60,1C@120" - a scancode and the clock tick to deliver it on.
        var script = new Queue<(long, byte)>();
        foreach (var step in (scancodes ?? string.Empty)
                     .Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = step.Split('@');
            script.Enqueue((long.Parse(parts[1]), Convert.ToByte(parts[0], 16)));
        }

        // ROCKFORD_TRACE=<linear hex> logs AX, CX, SI and DI every time that
        // address is executed. It is how the .SCM decoder got compared against
        // tools/scmformat.py iteration by iteration.
        var trace = new List<string>();
        int? traceAt = Environment.GetEnvironmentVariable("ROCKFORD_TRACE") is { } t
            ? Convert.ToInt32(t, 16) : null;

        var result = Harness.Run(machine, stopAt, limit, histogram: histogram, keys: script,
            timer: Environment.GetEnvironmentVariable("ROCKFORD_NO_IRQ") is null,
            until: Environment.GetEnvironmentVariable("ROCKFORD_UNTIL_FILES") is { } wantFiles
                ? _ => machine.Dos.Opened.Count >= int.Parse(wantFiles) : null,
            observeAt: traceAt,
            observe: mm =>
            {
                if (trace.Count >= 40_000) return;
                trace.Add($"{mm.Registers[Machine8086.AX]:X4} "
                          + $"{mm.Registers[Machine8086.CX]:X4} "
                          + $"{mm.Registers[Machine8086.SI]:X4} "
                          + $"{mm.Registers[Machine8086.DI]:X4}");
            });

        if (traceAt is not null)
        {
            File.WriteAllLines("/tmp/rk-trace.txt", trace);
            Console.WriteLine($"traced  {trace.Count:N0} visits to {traceAt:X5} -> /tmp/rk-trace.txt");
        }

        Console.WriteLine($"ran     {result.Instructions:N0} instructions"
                          + (result.Reached ? ", reached the address" : string.Empty)
                          + (machine.Machine.Stopped ? ", stopped" : string.Empty));
        Console.WriteLine($"now at  {result.Machine.Segments[Machine8086.CS]:X4}:"
                          + $"{result.Machine.Ip:X4}");

        if (result.Fault is { } fault)
        {
            Console.WriteLine($"FAULT   {fault}");
        }

        Console.WriteLine();
        Console.WriteLine($"modes   {string.Join(", ", result.Dos.Modes.Select(m => $"{m:X2}h"))}");
        Console.WriteLine($"opened  {Describe(result.Dos.Opened)}");
        if (result.Dos.OpenedFrom.Count > 0)
        {
            var (seg, off) = result.Dos.OpenedFrom[0];
            Console.WriteLine($"   from {seg:X4}:{off:X4}  linear {(seg << 4) + off:X5}");
        }

        if (result.Dos.Teletype.Length > 0)
        {
            Console.WriteLine($"screen  {result.Dos.Teletype.ToString().Replace("\r", "\\r").Replace("\n", "\\n")}");
        }

        if (result.Dos.KeyWaits > 0)
        {
            Console.WriteLine($"waited  {result.Dos.KeyWaits:N0} times for a key that was not queued");
        }

        if (result.Dos.Printed.Count > 0)
        {
            Console.WriteLine($"printed {string.Join(" | ", result.Dos.Printed)}");
        }

        // Broken down by function, not just by interrupt. "INT 10h x56" says
        // nothing; "AH=0Ch x50" says it is drawing pixels one at a time.
        foreach (var group in result.Dos.Calls.GroupBy(c => c.Interrupt).OrderBy(g => g.Key))
        {
            var functions = group
                .GroupBy(c => (byte)(c.Ax >> 8))
                .OrderByDescending(f => f.Count())
                .Select(f => $"AH={f.Key:X2}h x{f.Count():N0}");
            Console.WriteLine($"int {group.Key:X2}h {string.Join("  ", functions)}");
        }

        var pixels = result.Video.Pixels();
        var painted = pixels.Count(p => p != 0);
        Console.WriteLine($"pixels  {painted:N0} of {Display.Width * Display.Height:N0} are not colour 0");

        Console.WriteLine($"codeseg {(result.Dos.CodeSegment is { } cs2
            ? $"{cs2:X4}, load {Dos.LoadSegment:X4}, paragraph {cs2 - Dos.LoadSegment:X4}" : "not seen")}");
        Console.WriteLine($"hooked  {(result.Dos.Hooked.Count == 0 ? "nothing"
            : string.Join(", ", result.Dos.Hooked.Select(h => $"INT {h:X2}h")))}");
        Console.WriteLine($"raised  {(result.Machine.RaisedCounts.Count == 0 ? "nothing"
            : string.Join(", ", result.Machine.RaisedCounts.Select(r => $"INT {r.Key:X2}h x{r.Value:N0}")))}");
        if (Environment.GetEnvironmentVariable("ROCKFORD_HISTOGRAM") is { } path)
        {
            File.WriteAllLines(path, histogram.OrderBy(h => h.Key)
                .Select(h => $"{h.Key:X5} {h.Value}"));
        }

        // Where a stalled run is actually spending itself. The image unpacks
        // to a known base, so a linear address here can be turned straight
        // into a file offset in unpacked/ega.bin and looked up in PORT-PLAN.
        var hottest = histogram.OrderByDescending(h => h.Value).Take(6);
        Console.WriteLine($"hottest {string.Join("  ",
            hottest.Select(h => $"{h.Key:X5} x{h.Value:N0}"))}");
        if (result.Machine.Writes.Count > 0)
        {
            var writers = result.Machine.Writes
                .GroupBy(w => w.At)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => $"from IP {g.Key:X4} x{g.Count()}");
            Console.WriteLine($"wrote   {string.Join("  ", writers)}");
        }

        var ports = result.Machine.PortWrites
            .GroupBy(w => w.Port)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key:X3}h x{g.Count():N0}");
        Console.WriteLine($"ports   {string.Join("  ", ports)}");

        Console.WriteLine($"card    {result.Video.Exercised()}");

        if (Environment.GetEnvironmentVariable("ROCKFORD_MEMDUMP") is { } memory)
        {
            File.WriteAllBytes(memory, result.Machine.Memory);
            Console.WriteLine($"wrote   {memory}");
        }

        if (dump is not null)
        {
            // Indices then 256 RGB triples, because a screen is not a picture
            // without them: the same pixels under two palettes are two
            // different pictures, and that is exactly the kind of difference a
            // comparison has to be able to see.
            var colours = result.Video.Colours();
            var bytes = new byte[pixels.Length + colours.Length];
            pixels.CopyTo(bytes, 0);
            colours.CopyTo(bytes, pixels.Length);
            File.WriteAllBytes(dump, bytes);
            Console.WriteLine($"wrote   {dump}");
        }

        return result.Fault is null ? 0 : 1;
    }

    private static string Describe<T>(IReadOnlyList<T> items) =>
        items.Count == 0 ? "none" : string.Join(", ", items.Select(i => i?.ToString()));
}
