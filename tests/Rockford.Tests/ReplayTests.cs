using Rockford.Engine;

namespace Rockford.Tests;

/// <summary>
/// A recorded session, put back through a fresh host, must reproduce it exactly.
///
/// This is the claim #39 exists to make checkable. The port matches the
/// disassembly as read, and nothing compares it to a running original - but
/// "this engine reproduces ITSELF from the same inputs" is a different and
/// smaller claim, and it is the one every other piece of evidence rests on. A
/// fingerprint baseline is worthless if the same inputs do not give the same
/// run twice.
///
/// Measured with the same whole-state fingerprint the baselines use, rolled
/// forward across every call, so the ORDER is part of the answer and not only
/// the end state. Two runs that arrive at the same place by different routes
/// are not the same run.
/// </summary>
public class ReplayTests
{
    private const int Worlds = 2;
    private const int Records = Worlds * GameHost.CavesPerWorld;

    /// <summary>Synthetic caves, the same shape GameHostTests uses.</summary>
    private static (byte[] Cellmaps, CaveTables Tables, WorldEntry[] Worlds) Data(
        int treasure = 2, int seconds = 100)
    {
        var cellmaps = new byte[Records * CaveLoader.RecordSize];

        for (var record = 0; record < Records; record++)
        {
            var at = record * CaveLoader.RecordSize;

            for (var y = 0; y < CaveLoader.AuthoredHeight; y++)
            for (var x = 0; x < CaveLoader.AuthoredWidth; x++)
            {
                var edge = x == 0 || y == 0
                    || x == CaveLoader.AuthoredWidth - 1
                    || y == CaveLoader.AuthoredHeight - 1;
                cellmaps[at + y * CaveLoader.AuthoredWidth + x] = edge ? (byte)0x02 : (byte)0x00;
            }

            var row = at + 20 * CaveLoader.AuthoredWidth;
            cellmaps[row + 2] = 0x20;                       // the player
            for (var i = 0; i < treasure; i++)
                cellmaps[row + 3 + i] = 0x88;               // treasure
            cellmaps[row + 34] = 0x74;                      // the closed exit

            // A boulder up high, so something is falling while the player moves
            // and the run has state that is not only the player's own.
            cellmaps[at + 4 * CaveLoader.AuthoredWidth + 6] = 0x90;
        }

        var tables = new CaveTables
        {
            Required = Fill(treasure),
            Time = Fill(seconds),
            ScoreBefore = Fill(10),
            ScoreAfter = Fill(20),
            AmoebaCap = Fill(200),
        };

        WorldEntry[] worlds =
        [
            new() { Sheet = "one", World = 0 },
            new() { Sheet = "two", World = 1 },
        ];

        return (cellmaps, tables, worlds);
    }

    private static int[] Fill(int value) => [.. Enumerable.Repeat(value, 64)];

    /// <summary>
    /// A session with something of everything in it: the chooser, a world
    /// started, walking, firing, a restart, and a cave stepped past.
    /// </summary>
    private static void PlayASession(GameHost host)
    {
        host.ToMenu();
        host.MoveSelection(1);
        host.MoveSelection(-1);
        host.StartSelectedWorld();

        for (var i = 0; i < 12; i++) host.Tick(PlayerAction.Walk(PlayerAction.Right));
        for (var i = 0; i < 4; i++) host.Tick(PlayerAction.Shoot(PlayerAction.Right));
        for (var i = 0; i < 6; i++) host.Tick(PlayerAction.Walk(vertical: PlayerAction.Up));

        host.RestartCave();
        for (var i = 0; i < 10; i++) host.Tick(PlayerAction.Walk(PlayerAction.Left));

        host.Go(1);
        for (var i = 0; i < 8; i++) host.Tick(PlayerAction.Idle);
    }

    [Fact]
    public void A_replayed_session_reproduces_the_original_exactly()
    {
        var (cellmaps, tables, worlds) = Data();
        var identity = SessionIdentity.Of(cellmaps, tables, worlds);

        // Played once, with everything written down.
        var first = new GameHost(cellmaps, tables, worlds) { Recorder = [] };
        var firstPrint = new Fingerprint();
        RecordAndPrint(first, firstPrint);
        var recording = new SessionRecording(identity, first.Recorder!);

        // And again, from the recording alone, into a host that has never been
        // touched.
        var second = new GameHost(cellmaps, tables, worlds);
        var secondPrint = new Fingerprint();
        SessionPlayer.Replay(recording, second, identity,
            onCall: (_, _, h) => Absorb(secondPrint, h));

        Assert.Equal(firstPrint.ToString(), secondPrint.ToString());
    }

    [Fact]
    public void The_recording_holds_every_call_including_the_refused_ones()
    {
        var (cellmaps, tables, worlds) = Data();
        var host = new GameHost(cellmaps, tables, worlds) { Recorder = [] };

        // RestartCave on the title screen is refused - Screen is not Playing -
        // and it still has to be written down. A replay that dropped it would
        // diverge the moment the refusal stopped applying.
        Assert.False(host.RestartCave());

        Assert.Single(host.Recorder!);
        Assert.Equal(HostCallKind.RestartCave, host.Recorder![0].Kind);
    }

    [Fact]
    public void Ticks_are_recorded_before_the_early_returns()
    {
        var (cellmaps, tables, worlds) = Data();
        var host = new GameHost(cellmaps, tables, worlds) { Recorder = [] };

        // No simulation yet, so Tick returns immediately. The hold counters and
        // the frame count still advance on ticks like these, so a replay that
        // skipped them comes out of a death hold at a different moment.
        host.Tick(PlayerAction.Idle);
        host.Tick(PlayerAction.Idle);

        Assert.Equal(2, host.Recorder!.Count);
        Assert.All(host.Recorder!, c => Assert.Equal(HostCallKind.Tick, c.Kind));
    }

    [Fact]
    public void Only_calls_from_outside_are_recorded()
    {
        // Go calls Load itself, and StartSelectedWorld does too. Recording both
        // produces a list that cannot be replayed: applying Go runs Load by
        // itself and the recorded Load then runs it again.
        var (cellmaps, tables, worlds) = Data();
        var host = new GameHost(cellmaps, tables, worlds) { Recorder = [] };

        host.ToMenu();
        host.StartSelectedWorld();
        host.Go(1);

        Assert.Equal(
            [HostCallKind.ToMenu, HostCallKind.StartSelectedWorld, HostCallKind.Go],
            host.Recorder!.Select(c => c.Kind));
    }

    [Fact]
    public void Completing_a_cave_records_one_call_and_not_three()
    {
        // The worst case Astra named: a tick that finishes a cave reaches Go,
        // which reaches Load, all inside one call from outside. Recorded as
        // three and replayed, it walks to the cave after next before coming
        // back - loading a cave the session never loaded.
        var (cellmaps, tables, worlds) = Data(treasure: 1);
        var host = new GameHost(cellmaps, tables, worlds);

        host.ToMenu();
        host.StartSelectedWorld();

        // Collect the one gem and reach the exit, then let the hold expire.
        host.Recorder = [];
        for (var i = 0; i < 200 && host.Stage == 0; i++)
            host.Tick(PlayerAction.Walk(PlayerAction.Right));

        Assert.All(host.Recorder!, c => Assert.Equal(HostCallKind.Tick, c.Kind));
    }

    [Fact]
    public void A_nested_call_does_not_stop_the_next_outside_one_being_recorded()
    {
        // The counter has to come back down, or the first nested call silences
        // the recorder for the rest of the session.
        var (cellmaps, tables, worlds) = Data();
        var host = new GameHost(cellmaps, tables, worlds) { Recorder = [] };

        host.StartSelectedWorld();      // nests a Load
        host.ToMenu();
        host.MoveSelection(1);

        Assert.Equal(3, host.Recorder!.Count);
    }

    [Fact]
    public void An_action_survives_being_packed_and_unpacked()
    {
        // The original's own encoding, so a packed action can be read by eye
        // against the disassembly: horizontal in the low byte, vertical in the
        // next, fire above both.
        foreach (var action in new[]
                 {
                     PlayerAction.Idle,
                     PlayerAction.Walk(PlayerAction.Left),
                     PlayerAction.Walk(PlayerAction.Right, PlayerAction.Up),
                     PlayerAction.Shoot(PlayerAction.Left, PlayerAction.Down),
                     PlayerAction.Shoot(),
                 })
        {
            Assert.Equal(action, HostCall.Unpack(HostCall.Pack(action)));
        }
    }

    [Fact]
    public void Replaying_against_different_caves_is_refused()
    {
        // The worst way for this to fail is quietly: a recording run against
        // the wrong data diverges, and the divergence looks like an engine
        // fault rather than like the wrong file.
        var (cellmaps, tables, worlds) = Data();
        var (other, otherTables, _) = Data(treasure: 3);

        var recording = new SessionRecording(
            SessionIdentity.Of(cellmaps, tables, worlds), [HostCall.ToMenu()]);

        var host = new GameHost(other, otherTables, worlds);
        var wrong = SessionIdentity.Of(other, otherTables, worlds);

        var thrown = Assert.Throws<InvalidOperationException>(
            () => SessionPlayer.Replay(recording, host, wrong));
        Assert.Contains("different caves", thrown.Message);
    }

    [Fact]
    public void Replaying_against_different_tables_is_refused()
    {
        var (cellmaps, tables, worlds) = Data();
        var (_, otherTables, _) = Data(seconds: 200);

        var recording = new SessionRecording(
            SessionIdentity.Of(cellmaps, tables, worlds), [HostCall.ToMenu()]);

        var host = new GameHost(cellmaps, otherTables, worlds);
        var wrong = SessionIdentity.Of(cellmaps, otherTables, worlds);

        var thrown = Assert.Throws<InvalidOperationException>(
            () => SessionPlayer.Replay(recording, host, wrong));
        Assert.Contains("different cave tables", thrown.Message);
    }

    [Fact]
    public void Setting_the_screen_or_the_selection_is_recorded()
    {
        // Both are public setters and both escaped the recorder entirely. The
        // browser sets Selected directly when the chooser opens on the world
        // just played, so a session that used the chooser replayed with the
        // wrong world highlighted.
        var (cellmaps, tables, worlds) = Data();
        var host = new GameHost(cellmaps, tables, worlds) { Recorder = [] };

        host.Selected = 1;
        host.Screen = GameScreen.Menu;

        Assert.Equal(
            [HostCallKind.SetSelected, HostCallKind.SetScreen],
            host.Recorder!.Select(c => c.Kind));
        Assert.Equal(1, host.Recorder![0].A);
        Assert.Equal((int)GameScreen.Menu, host.Recorder![1].A);
    }

    [Fact]
    public void The_hosts_own_screen_changes_are_not_recorded()
    {
        // ToMenu sets Screen itself. Recording that as well would replay the
        // transition and then set the screen again, which is the same
        // double-application the nesting guard exists to stop.
        var (cellmaps, tables, worlds) = Data();
        var host = new GameHost(cellmaps, tables, worlds) { Recorder = [] };

        host.ToMenu();

        Assert.Single(host.Recorder!);
        Assert.Equal(HostCallKind.ToMenu, host.Recorder![0].Kind);
    }

    [Fact]
    public void Replaying_against_a_different_world_list_is_refused()
    {
        // The world list was recorded as a COUNT and the count was never
        // compared. Reorder worlds.json, or change one entry's raw World
        // number, and a replay would be accepted and then load a different
        // cave record for the same call.
        var (cellmaps, tables, worlds) = Data();

        WorldEntry[] reordered =
        [
            new() { Sheet = "two", World = 1 },
            new() { Sheet = "one", World = 0 },
        ];

        var recording = new SessionRecording(
            SessionIdentity.Of(cellmaps, tables, worlds), [HostCall.ToMenu()]);

        var host = new GameHost(cellmaps, tables, reordered);
        var wrong = SessionIdentity.Of(cellmaps, tables, reordered);

        var thrown = Assert.Throws<InvalidOperationException>(
            () => SessionPlayer.Replay(recording, host, wrong));
        Assert.Contains("different world list", thrown.Message);
    }

    [Fact]
    public void A_session_that_crosses_a_cave_boundary_replays_exactly()
    {
        // The case the first version could not have caught: finishing a cave
        // nests Tick into Go into Load, and the state that tells them apart -
        // which cave you are on - was not in the fingerprint either.
        var (cellmaps, tables, worlds) = Data(treasure: 1);
        var identity = SessionIdentity.Of(cellmaps, tables, worlds);

        var first = new GameHost(cellmaps, tables, worlds) { Recorder = [] };
        first.ToMenu();
        first.StartSelectedWorld();
        for (var i = 0; i < 300; i++) first.Tick(PlayerAction.Walk(PlayerAction.Right));

        // It has to actually get somewhere, or this asserts nothing.
        Assert.True(first.Stage > 0 || first.Session.Score > 0,
            "the session never finished a cave, so this proves nothing");

        var recording = new SessionRecording(identity, first.Recorder!);

        var second = new GameHost(cellmaps, tables, worlds);
        SessionPlayer.Replay(recording, second, identity);

        var a = new Fingerprint();
        Absorb(a, first);
        var b = new Fingerprint();
        Absorb(b, second);

        Assert.Equal(first.Stage, second.Stage);
        Assert.Equal(a.ToString(), b.ToString());
    }

    [Fact]
    public void A_replay_can_be_stopped_part_way()
    {
        // What an inspector needs: run to call N and look at the state there.
        var (cellmaps, tables, worlds) = Data();
        var identity = SessionIdentity.Of(cellmaps, tables, worlds);

        var played = new GameHost(cellmaps, tables, worlds) { Recorder = [] };
        PlayASession(played);
        var recording = new SessionRecording(identity, played.Recorder!);

        var host = new GameHost(cellmaps, tables, worlds);
        var applied = SessionPlayer.Replay(recording, host, identity, upTo: 5);

        Assert.Equal(5, applied);

        // And stopping early is the same as a shorter recording, which is what
        // makes stepping through one trustworthy.
        var shorter = new SessionRecording(identity, [.. recording.Calls.Take(5)]);
        var alsoHost = new GameHost(cellmaps, tables, worlds);
        SessionPlayer.Replay(shorter, alsoHost, identity);

        var a = new Fingerprint();
        Absorb(a, host);
        var b = new Fingerprint();
        Absorb(b, alsoHost);
        Assert.Equal(a.ToString(), b.ToString());
    }

    [Fact]
    public void Replaying_does_not_record_over_the_recording()
    {
        // Attaching a recorder and then replaying into the same host would
        // append the replayed calls to the ones already there, doubling the
        // session and making the next replay wrong.
        var (cellmaps, tables, worlds) = Data();
        var identity = SessionIdentity.Of(cellmaps, tables, worlds);

        var recording = new SessionRecording(identity, [HostCall.ToMenu(), HostCall.ToMenu()]);
        var host = new GameHost(cellmaps, tables, worlds) { Recorder = [] };

        SessionPlayer.Replay(recording, host, identity);

        Assert.Null(host.Recorder);
    }

    /// <summary>Plays the session on a recording host, printing as it goes.</summary>
    private static void RecordAndPrint(GameHost host, Fingerprint print)
    {
        // The same calls PlayASession makes, absorbed one at a time so the two
        // sides of the comparison see the state at the same moments.
        var recorder = host.Recorder!;
        var seen = 0;

        void Step(Action act)
        {
            act();
            while (seen < recorder.Count)
            {
                seen++;
                Absorb(print, host);
            }
        }

        Step(() => host.ToMenu());
        Step(() => host.MoveSelection(1));
        Step(() => host.MoveSelection(-1));
        Step(() => host.StartSelectedWorld());

        for (var i = 0; i < 12; i++) Step(() => host.Tick(PlayerAction.Walk(PlayerAction.Right)));
        for (var i = 0; i < 4; i++) Step(() => host.Tick(PlayerAction.Shoot(PlayerAction.Right)));
        for (var i = 0; i < 6; i++) Step(() => host.Tick(PlayerAction.Walk(vertical: PlayerAction.Up)));

        Step(() => host.RestartCave());
        for (var i = 0; i < 10; i++) Step(() => host.Tick(PlayerAction.Walk(PlayerAction.Left)));

        Step(() => host.Go(1));
        for (var i = 0; i < 8; i++) Step(() => host.Tick(PlayerAction.Idle));
    }

    [Fact]
    public void A_recording_survives_a_round_trip_through_a_file()
    {
        var (cellmaps, tables, worlds) = Data();
        var identity = SessionIdentity.Of(cellmaps, tables, worlds);

        var played = new GameHost(cellmaps, tables, worlds) { Recorder = [] };
        PlayASession(played);
        var recording = new SessionRecording(identity, played.Recorder!);

        var back = SessionRecordingFile.FromJson(SessionRecordingFile.ToJson(recording));

        Assert.NotNull(back);
        Assert.Equal(recording.Identity, back!.Identity);
        Assert.Equal(recording.Calls.Count, back.Calls.Count);
        Assert.Equal(recording.Calls, back.Calls);
    }

    [Fact]
    public void A_recording_read_from_a_file_replays_identically()
    {
        // The round trip above proves the calls come back. This proves the
        // thing that matters: a session that has been through a file gives the
        // same run as one that has not.
        var (cellmaps, tables, worlds) = Data();
        var identity = SessionIdentity.Of(cellmaps, tables, worlds);

        var played = new GameHost(cellmaps, tables, worlds) { Recorder = [] };
        PlayASession(played);
        var recording = new SessionRecording(identity, played.Recorder!);
        var back = SessionRecordingFile.FromJson(SessionRecordingFile.ToJson(recording))!;

        var a = new GameHost(cellmaps, tables, worlds);
        var aPrint = new Fingerprint();
        SessionPlayer.Replay(recording, a, identity, onCall: (_, _, h) => Absorb(aPrint, h));

        var b = new GameHost(cellmaps, tables, worlds);
        var bPrint = new Fingerprint();
        SessionPlayer.Replay(back, b, identity, onCall: (_, _, h) => Absorb(bPrint, h));

        Assert.Equal(aPrint.ToString(), bPrint.ToString());
    }

    [Fact]
    public void A_recording_from_a_later_version_is_refused()
    {
        // Without a version, a file from a later build deserialises into
        // something plausible and wrong, which is the failure mode a format
        // without one always eventually has.
        var json = SessionRecordingFile.ToJson(new SessionRecording(
            SessionIdentity.Of([1], new CaveTables(), []), [HostCall.ToMenu()]))
            .Replace("\"Version\": 1", "\"Version\": 2");

        var thrown = Assert.Throws<InvalidOperationException>(
            () => SessionRecordingFile.FromJson(json));
        Assert.Contains("version 2", thrown.Message);
    }

    private static void Absorb(Fingerprint print, GameHost host)
    {
        print.Absorb(host);
        if (host.Sim is not null) print.Absorb(host.Sim);
    }
}
