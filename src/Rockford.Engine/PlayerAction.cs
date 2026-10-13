namespace Rockford.Engine;

/// <summary>
/// One frame of input, in the original's own encoding.
///
/// Horizontal and vertical are separate bytes, which is why a diagonal press has a
/// defined meaning. <c>ds:0x164E</c> only ever holds 0, 4 or 8; up and down live in
/// <c>ds:0x1650</c> as 1 and 2.
/// </summary>
public readonly record struct PlayerAction(byte Horizontal, byte Vertical, bool Fire)
{
    public const byte None = 0;
    public const byte Left = 4;
    public const byte Right = 8;
    public const byte Up = 1;
    public const byte Down = 2;

    public static readonly PlayerAction Idle = new(None, None, false);

    public static PlayerAction Walk(byte horizontal = None, byte vertical = None) =>
        new(horizontal, vertical, false);

    public static PlayerAction Shoot(byte horizontal = None, byte vertical = None) =>
        new(horizontal, vertical, true);

    public bool AnyDirection => Horizontal != None || Vertical != None;
}
