namespace Rockford.Engine;

public sealed partial class Simulation
{
    /// <summary>
    /// A cave step can move a lot at once - a column of boulders, a blast, and the
    /// player - but not without bound, and a host that never drains should not be
    /// able to grow this for the life of a session.
    ///
    /// Well above anything a real step produces. Reaching it means the host has
    /// stopped draining, and dropping the overflow is better than growing for ever.
    /// </summary>
    private const int MaxObservations = 512;

    private readonly List<Observation> _observations = new(64);

    /// <summary>
    /// Records something that has already happened. Appends and nothing else.
    ///
    /// <para>
    /// This is the only way anything reaches <see cref="TakeObservations"/>, and it
    /// is deliberately the dullest method in the engine: no branch on simulation
    /// state, no write to the grid, no draw from <see cref="Random"/>. That is what
    /// makes "the observation surface cannot change the simulation" checkable by
    /// reading one method rather than by trusting every call site.
    /// </para>
    /// </summary>
    private void Observe(Observation what)
    {
        if (_observations.Count >= MaxObservations) return;
        _observations.Add(what);
    }

    /// <summary>
    /// Everything observed since the last call, oldest first, and clears it.
    ///
    /// <para>
    /// Drained on take rather than cleared at the top of <see cref="Step"/>, and
    /// that is the important difference. <c>GameHost.Tick</c> calls
    /// <c>PlayerTick</c> BEFORE <c>Step</c>, so a buffer cleared at the top of the
    /// scan would throw away everything the player just did - their own move, a
    /// push, a shot - every single step. Draining on take has no such ordering to
    /// get wrong: whatever happened since the host last looked is what it gets.
    /// </para>
    ///
    /// <para>
    /// One caller. Two front ends both taking this would each get half of a step.
    /// </para>
    /// </summary>
    public IReadOnlyList<Observation> TakeObservations()
    {
        var seen = _observations.ToArray();
        _observations.Clear();
        return seen;
    }
}
