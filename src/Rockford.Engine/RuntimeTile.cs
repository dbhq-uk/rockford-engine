namespace Rockford.Engine;

/// <summary>
/// The value byte of a runtime cell.
///
/// These are the original's own numbers, not a re-encoding. Every one is taken from
/// the dispatch table at file <c>0x13C50</c>, which the scan loop indexes with
/// <c>call WORD PTR cs:[di+0x5260]</c> at <c>0x14102</c> where <c>di</c> is twice
/// the value byte. Values below 4 skip the dispatch and never act.
///
/// Where a name is uncertain the comment says so. The original display names are not
/// recoverable from the executable.
/// </summary>
public static class RuntimeTile
{
    // Inert. The scan skips the dispatch for anything below 4.
    public const byte Space = 0x00;
    public const byte Dirt = 0x01;
    public const byte Steel = 0x02;
    public const byte Wall = 0x03;

    /// <summary>Authored <c>0C</c>. No handler of its own, but falling objects test
    /// it and may pass through unchanged. Handler is a bare return.</summary>
    public const byte PassThroughWall = 0x04;

    /// <summary>Closed exit. Handler <c>0x14316</c> opens it when the quota is met.</summary>
    public const byte ExitClosed = 0x07;

    /// <summary>Authored <c>C4</c>. The acid tap, four animation states, emitting
    /// downward once armed. Handler <c>0x14445</c>.</summary>
    public const byte ProducerDown = 0x08;
    public const byte AcidTap = 0x08;
    public const byte AcidTapLast = 0x0B;

    /// <summary>Authored <c>38</c>. The fire tap, the same but upward, and four steps
    /// out of phase with the acid tap. Handler <c>0x140AE</c>.</summary>
    public const byte ProducerUp = 0x0C;
    public const byte FireTap = 0x0C;
    public const byte FireTapLast = 0x0F;

    /// <summary>The player. The loader writes <c>0x1110</c> here and <c>0x1111</c>
    /// into the cell's second plane at <c>+0x80</c>.</summary>
    public const byte Player = 0x10;

    /// <summary>The player's second plane value, written at <c>+0x80</c>.
    /// It shares the number of <see cref="TreasureBlast0"/>, which is harmless: the
    /// scan only visits the leftmost 40 columns of a row and the second plane sits
    /// 64 cells to the right, so it is never dispatched.</summary>
    public const byte PlayerShadow = 0x11;

    // Explosion that resolves to treasure. Phases advance one per visit at 0x1467C,
    // and 0x14126 turns the last one into treasure.
    public const byte TreasureBlast0 = 0x11;
    public const byte TreasureBlast1 = 0x12;
    public const byte TreasureBlast2 = 0x13;
    public const byte TreasureBlast3 = 0x14;

    /// <summary>Expanding wall. Authored <c>08</c>. Handler <c>0x14632</c>.</summary>
    public const byte ExpandingWall = 0x15;

    /// <summary>Expanding wall that grew this scan and must not grow again.
    /// Handler <c>0x14677</c>.</summary>
    public const byte ExpandingWallDeferred = 0x16;

    // Explosion that resolves to space. 0x14130 turns the last one into space.
    public const byte SpaceBlast0 = 0x1B;
    public const byte SpaceBlast1 = 0x1C;
    public const byte SpaceBlast2 = 0x1D;
    public const byte SpaceBlast3 = 0x1E;

    /// <summary>What fire leaves in a dug cell. Not a separate state: it is the
    /// second phase of the space explosion, so dug dirt joins that chain part way
    /// through and reaches space a visit sooner than a blast does.</summary>
    public const byte DigEffect = SpaceBlast1;

    // ---- Acid: what the acid tap emits, falling one cell per four-state cycle ----

    /// <summary>The head of the acid chain, <c>1F</c> to <c>22</c>, terminal <c>23</c>.</summary>
    public const byte AcidHead = 0x1F;

    /// <summary>Acid that could not fall, from <c>0x14349</c>. Chain <c>24</c>.</summary>
    public const byte AcidBlocked1 = 0x24;

    /// <summary>Still blocked, from <c>0x146B3</c>. Chain <c>25</c>.</summary>
    public const byte AcidBlocked2 = 0x25;

    /// <summary>The second acid chain, <c>28</c> to <c>2A</c>, terminal <c>2B</c>.</summary>
    public const byte AcidSettled = 0x28;

    /// <summary>The whole span fire tests against at <c>0x1448A</c>, so that fire meets
    /// acid at any point in the acid's life.</summary>
    public const byte AcidRangeFirst = 0x20;
    public const byte AcidRangeLast = 0x2B;

    // ---- Fire: what the fire tap emits, rising, and going sideways when it cannot ----

    /// <summary>The head of the fire chain, <c>2C</c> to <c>2E</c>, terminal <c>2F</c>.</summary>
    public const byte FireHead = 0x2C;

    /// <summary>State byte meaning "this fire is travelling left". <c>0x14504</c>.</summary>
    public const byte FireGoingLeft = 0xF8;

    /// <summary>State byte meaning "travelling right". <c>0x14518</c>.</summary>
    public const byte FireGoingRight = 0xFA;

    /// <summary>Settled acid uses the same two state bytes for the same purpose,
    /// written at <c>0x143C2</c> and <c>0x143D6</c>. Named separately because the two
    /// projectiles are otherwise unrelated.</summary>
    public const byte AcidGoingLeft = 0xF8;
    public const byte AcidGoingRight = 0xFA;

    // ---- Deferred seeds, 0xF0 to 0xF4 ----
    //
    // Each becomes its real value on the next visit and does nothing else. They exist
    // for the same reason as the deferred boulder and treasure states: a handler that
    // writes into a cell the scan has not reached yet must write something inert, or
    // the thing it just created acts again in the same step.

    /// <summary>Becomes <see cref="AcidSettled"/>. Handler <c>0x1443D</c>.</summary>
    public const byte SeedAcidSettled = 0xF0;

    /// <summary>Becomes <see cref="FireHead"/>. Handler <c>0x145AF</c>.</summary>
    public const byte SeedFireHead = 0xF1;

    /// <summary>Becomes <see cref="AcidBlocked2"/>. Handler <c>0x146D0</c>.</summary>
    public const byte SeedAcidBlocked2 = 0xF2;

    /// <summary>Becomes <c>A7</c>, the first worm's last body segment. Handler
    /// <c>0x13ED4</c>. Written by the movement routine at <c>0x119B6</c> on the cell
    /// a two-cell worm is leaving, which is the cell that becomes its tail.</summary>
    public const byte SeedA7 = 0xF3;

    /// <summary>Becomes <c>B7</c>. Handler <c>0x1460F</c>, written at <c>0x11AD3</c>,
    /// the same thing for the second worm.</summary>
    public const byte SeedB7 = 0xF4;

    /// <summary>Open exit, four facings. Handler <c>0x14301</c>.</summary>
    public const byte ExitOpen = 0x30;
    public const byte ExitOpenLast = 0x33;

    /// <summary>Resting treasure. Handler <c>0x1414A</c>.</summary>
    public const byte Treasure = 0x40;

    /// <summary>Falling treasure. Handler <c>0x14223</c>.</summary>
    public const byte TreasureFalling = 0x42;

    /// <summary>Treasure that started falling this scan. Handler <c>0x142F7</c>
    /// turns it back into <see cref="TreasureFalling"/> on the next visit.</summary>
    public const byte TreasureFallingDeferred = 0x43;

    /// <summary>Resting boulder. Handler <c>0x13EF7</c>.</summary>
    public const byte Boulder = 0x44;

    /// <summary>Falling boulder. Handler <c>0x13FD0</c>.</summary>
    public const byte BoulderFalling = 0x46;

    /// <summary>Boulder that started falling this scan. Handler <c>0x140A4</c>
    /// turns it back into <see cref="BoulderFalling"/> on the next visit.</summary>
    public const byte BoulderFallingDeferred = 0x47;

    /// <summary>Amoeba, authored <c>10</c>, four states. Handler <c>0x13E50</c>.</summary>
    public const byte Amoeba = 0x48;
    public const byte AmoebaLast = 0x4B;

    /// <summary>The timed converting wall, authored <c>04</c>. Four states.
    /// Handler <c>0x1467F</c>. This is what the project used to call a magic wall,
    /// attached to the wrong authored byte.</summary>
    public const byte TimedWall = 0x74;
    public const byte TimedWallLast = 0x77;

    /// <summary>Authored <c>70</c>. Timed entity tracked at <c>DS:1614</c>.
    /// Handler <c>0x13ED8</c>.</summary>
    public const byte TimedEntityA = 0x78;

    /// <summary>The last of the first entity's four dormant frames.</summary>
    public const byte TimedEntityALast = 0x7B;

    /// <summary>
    /// The first worm's newest body cell, from <c>0x13EE0</c>. Authored <c>0x70</c>
    /// is the evil worm: it turns treasure back into boulders and boulders into
    /// <see cref="EnemyAnticlockwise"/>, the one that leaves nothing behind.
    ///
    /// The eight values <c>A0</c> to <c>A7</c> are a segment's age, not a facing.
    /// The scan at <c>0x13EBB</c> ages every one of them by a step.
    /// </summary>
    public const byte WormA = 0xA0;

    /// <summary>The first worm's oldest body cell - its tail. <c>0x13E83</c>.</summary>
    public const byte WormALast = 0xA7;

    /// <summary>Authored <c>84</c>. Second timed entity, tracked at <c>DS:161C</c>.
    /// Handler <c>0x14613</c>. The old engine mistook this for the amoeba.</summary>
    public const byte TimedEntityB = 0x7C;

    /// <summary>The last of the second entity's four dormant frames.</summary>
    public const byte TimedEntityBLast = 0x7F;

    /// <summary>
    /// The second worm's newest body cell, from <c>0x1461B</c>. Authored <c>0x84</c>
    /// is the good worm and it walks the same two chains the other way:
    /// <see cref="EnemyAnticlockwise"/> back to a boulder, and a boulder on to
    /// treasure.
    /// </summary>
    public const byte WormB = 0xB0;

    /// <summary>The second worm's oldest body cell - its tail. <c>0x145BE</c>.</summary>
    public const byte WormBLast = 0xB7;

    /// <summary>Enemy family that prefers a clockwise turn. Handler <c>0x11B54</c>.
    /// Explodes into treasure.</summary>
    public const byte EnemyClockwise = 0x8C;

    /// <summary>The clockwise family's just-moved state. Handler <c>0x140DA</c>.</summary>
    public const byte EnemyClockwiseMoved = 0x8D;

    /// <summary>Enemy family that prefers an anticlockwise turn. Handler <c>0x11B91</c>.
    /// Explodes into space.</summary>
    public const byte EnemyAnticlockwise = 0x90;

    /// <summary>The anticlockwise family's just-moved state. Handler <c>0x1446F</c>.</summary>
    public const byte EnemyAnticlockwiseMoved = 0x91;

    /// <summary>True for either enemy family, ignoring the just-moved states.</summary>
    public static bool IsEnemy(byte v) => v is EnemyClockwise or EnemyAnticlockwise;

    /// <summary>True for the player's own two values.</summary>
    public static bool IsPlayer(byte v) => v is Player or PlayerShadow;

    /// <summary>True for the amoeba's four states.</summary>
    public static bool IsAmoeba(byte v) => v >= Amoeba && v <= AmoebaLast;

    /// <summary>True for the timed converting wall's four states.</summary>
    public static bool IsTimedWall(byte v) => v >= TimedWall && v <= TimedWallLast;

    /// <summary>True for any of the acid tap's or fire tap's four states.</summary>
    public static bool IsTap(byte v) => v >= AcidTap && v <= FireTapLast;

    /// <summary>True for the open exit's four facings.</summary>
    public static bool IsExitOpen(byte v) => v >= ExitOpen && v <= ExitOpenLast;

    /// <summary>
    /// Which kind of thing a value is, for deciding whether the object that moved
    /// into a cell is still the object sitting in it.
    ///
    /// <para>
    /// A pure classification of the value bytes. It decides nothing, it is not
    /// consulted by any rule, and it exists for one caller: the renderer, which
    /// animates a move by pulling the destination's sprite back towards the
    /// source. A move is reported the moment it happens, and the rest of the scan
    /// can then blow that cell up, so the sprite there at the end of the step is
    /// not always the one that arrived. This is how that is caught.
    /// </para>
    ///
    /// <para>
    /// The families deliberately span a thing's states. A boulder reported moving
    /// is <see cref="Boulder"/>, but the cell it lands in holds
    /// <see cref="BoulderFalling"/> or its deferred form, because the scan order
    /// writes one of those - comparing the values outright would reject every
    /// falling object there is.
    /// </para>
    ///
    /// <para>
    /// <b>This is a filter, not a proof of identity.</b> It cannot tell a boulder
    /// that moved in from a different boulder that replaced it in the same step.
    /// It catches the case that matters - the thing that moved was destroyed or
    /// transformed - and the caller's fallback for everything else is to snap,
    /// which is always safe.
    /// </para>
    /// </summary>
    public static int FamilyOf(byte v) => v switch
    {
        Player or PlayerShadow => 1,
        Boulder or BoulderFalling or BoulderFallingDeferred => 2,
        Treasure or TreasureFalling or TreasureFallingDeferred => 3,
        EnemyClockwise or EnemyClockwiseMoved => 4,
        EnemyAnticlockwise or EnemyAnticlockwiseMoved => 5,
        >= AcidHead and <= AcidRangeLast => 6,
        >= FireHead and <= 0x2F => 7,      // the fire chain, 2C to its terminal 2F
        _ => 0,
    };

    /// <summary>
    /// True when two values are the same kind of thing. Family zero is everything
    /// unclassified and never matches, including itself, so an unknown value snaps
    /// rather than animating on a coincidence.
    /// </summary>
    public static bool SameFamily(byte a, byte b)
    {
        var family = FamilyOf(a);
        return family != 0 && family == FamilyOf(b);
    }
}
