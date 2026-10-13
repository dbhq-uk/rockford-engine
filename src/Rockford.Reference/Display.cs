namespace Rockford.Reference;

/// <summary>
/// An EGA, to the depth this game uses one.
/// </summary>
/// <remarks>
/// <para>
/// This is the part with no equivalent in the Alley Cat harness. A CGA's
/// framebuffer <i>is</i> its memory: the program writes bytes at B800 and those
/// bytes are the picture, so comparing two runs is comparing two 16 KB arrays.
/// An EGA is not like that. Mode 0Dh is 320x200 in 16 colours, which is four
/// bits a pixel, which is four separate 8,000-byte planes sitting behind a
/// single aperture at A000. A byte written there can land in any subset of the
/// four, can be combined with what was already in them, and can be masked to
/// individual bits - all decided by registers the write instruction never
/// mentions.
/// </para>
/// <para>
/// Which is why a 32,000-byte <c>.FIL</c> on the original disk is exactly one
/// screen: 4 planes x 8,000 bytes. The file format is the card.
/// </para>
/// <para>
/// <b>The latches are the thing to get right.</b> Every read from the aperture
/// loads all four planes into four internal registers, and most of the write
/// modes combine against those rather than against anything the CPU supplied.
/// That is how the original moves a 16x16 tile: read a byte to fill the
/// latches, write a byte to place them, and the value written is often
/// irrelevant. A model that treats the aperture as ordinary memory gets a
/// picture that is roughly right, which is the failure this whole harness
/// exists to avoid.
/// </para>
/// </remarks>
public sealed class Display : IPeripherals
{
    /// <summary>
    /// True once the BIOS has been asked for mode 13h.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The two original releases use two different cards, and the port was
    /// written against the second.</b> The EGA build sets mode 0Dh and the
    /// planar model below is what it needs. The VGA build sets mode 13h, which
    /// is 320x200 in 256 colours with one byte a pixel and no planes at all -
    /// the aperture IS the picture, the way a CGA's is.
    /// </para>
    /// <para>
    /// That matters more than it looks: the routine addresses cited in
    /// <c>docs/PORT-PLAN.md</c> and in <c>src/Rockford.Engine</c> are VGA file
    /// offsets, and <c>unpacked/ega.bin</c> holds twelve zero bytes at all six
    /// of the ones <c>tools/cicodis-check.py</c> asserts. So a harness that
    /// only ran the EGA build could not compare the documented simulation at
    /// all.
    ///
    /// <b>Not every address in those documents, though.</b>
    /// <c>docs/PORT-PLAN.md</c> line 581 cites <c>0x1FAF3</c> as an EGA
    /// routine, and the <c>JNS</c> it describes is there in
    /// <c>unpacked/ega.bin</c> while the VGA image ends at <c>0x14B40</c>. The
    /// six-offset assertion holds; the universal wording did not.
    /// </para>
    /// </remarks>
    public bool Linear => Mode == 0x13;

    /// <summary>The 256 colour registers, six bits a channel as the DAC holds them.</summary>
    public byte[] Dac { get; } = new byte[256 * 3];

    private int _dacIndex;
    private int _dacChannel;
    private bool _dacReading;

    /// <summary>Bytes per plane in mode 0Dh: 320x200 at one bit a pixel.</summary>
    public const int PlaneLength = 8000;

    public const int Width = 320;
    public const int Height = 200;

    /// <summary>The aperture, which is bigger than the mode needs.</summary>
    public const int ApertureLength = 0x10000;

    public const int ApertureBase = Machine8086.VideoSegment << 4;

    /// <summary>The four planes. Index is plane, then offset within it.</summary>
    public byte[][] Planes { get; } =
        [new byte[ApertureLength], new byte[ApertureLength],
         new byte[ApertureLength], new byte[ApertureLength]];

    /// <summary>The four latches, loaded by every read from the aperture.</summary>
    public byte[] Latches { get; } = new byte[4];

    // Sequencer, at 3C4/3C5.
    private int _sequencerIndex;
    public byte MapMask { get; private set; } = 0x0F;

    // Graphics controller, at 3CE/3CF.
    private int _graphicsIndex;
    public byte SetReset { get; private set; }
    public byte EnableSetReset { get; private set; }
    public byte ColourCompare { get; private set; }
    public byte DataRotate { get; private set; }
    public byte ReadMapSelect { get; private set; }
    public byte GraphicsMode { get; private set; }
    public byte Miscellaneous { get; private set; } = 0x05;
    public byte ColourDontCare { get; private set; } = 0x0F;
    public byte BitMask { get; private set; } = 0xFF;

    // Attribute controller, at 3C0. A flip-flop decides whether a write to
    // 3C0 is an index or a value, and reading 3DA resets it to "index" - which
    // is why the status register gets read in the middle of palette code that
    // has nothing to do with the beam.
    private bool _attributeExpectsIndex = true;
    private int _attributeIndex;
    public byte[] Attributes { get; } = new byte[0x20];

    // CRTC, at 3D4/3D5. Recorded, because the start address decides which
    // part of the planes is on screen and two identical plane sets can still
    // be two different pictures.
    private int _crtcIndex;
    public byte[] Crtc { get; } = new byte[0x20];

    /// <summary>The mode the BIOS was last asked for, or -1 before any call.</summary>
    public int Mode { get; set; } = -1;

    /// <summary>The machine, for the retrace the status register reports.</summary>
    public Machine8086? Machine { get; set; }

    /// <summary>The start address the CRTC is displaying from.</summary>
    public int StartAddress => Crtc[0x0C] << 8 | Crtc[0x0D];

    private byte WriteMode => (byte)(GraphicsMode & 3);

    private bool ReadModeIsCompare => (GraphicsMode & 0x08) != 0;

    /// <summary>True when an address falls in the aperture this card answers.</summary>
    public static bool Owns(int linear) =>
        linear >= ApertureBase && linear < ApertureBase + ApertureLength;

    /// <summary>
    /// A read from the aperture, which always loads the latches.
    /// </summary>
    /// <remarks>
    /// Always, including the read half of a read-modify-write and including a
    /// read whose value is thrown away. Code that fills the latches does
    /// exactly that - <c>mov al, [si]</c> with nothing looking at <c>al</c> -
    /// and a model that only latched on a "real" read would leave the next
    /// write placing whatever was there before.
    /// </remarks>
    public byte ReadAperture(int offset)
    {
        // Mode 13h has no planes, no latches and no read map select. This
        // early return was written with the linear path and did not land;
        // without it, writing a byte and reading it back through a non-zero
        // read map answered zero.
        if (Linear) return Planes[0][offset];

        Latched++;

        for (var plane = 0; plane < 4; plane++)
        {
            Latches[plane] = Planes[plane][offset];
        }

        if (!ReadModeIsCompare)
        {
            return Latches[ReadMapSelect & 3];
        }

        // Read mode 1: a bit is set where every plane we care about matches
        // the colour compare register. The game does not appear to use it;
        // it is here because leaving it out would mean answering a read mode
        // with the wrong mode's answer, silently.
        byte result = 0;
        for (var bit = 0; bit < 8; bit++)
        {
            var matches = true;
            for (var plane = 0; plane < 4; plane++)
            {
                if ((ColourDontCare >> plane & 1) == 0) continue;

                var got = Latches[plane] >> bit & 1;
                var want = ColourCompare >> plane & 1;
                if (got != want) matches = false;
            }

            if (matches) result |= (byte)(1 << bit);
        }

        return result;
    }

    /// <summary>
    /// What the program has actually used this card for.
    /// </summary>
    /// <remarks>
    /// <b>A passing comparison is worth exactly what the run exercised.</b>
    /// Breaking the bit mask on purpose and watching the title-screen test
    /// still pass is how this got written: that path leaves the mask at FFh
    /// throughout, so the assertion covered the write mode, the map mask and
    /// the plane layout and said nothing whatever about masking. Counting the
    /// features a run touched is the difference between "it matched" and "it
    /// matched while the card was doing something".
    /// </remarks>
    public long[] WriteModeUses { get; } = new long[4];

    public long[] FunctionUses { get; } = new long[4];

    /// <summary>Writes that went through a bit mask other than FFh.</summary>
    public long MaskedWrites { get; private set; }

    /// <summary>Writes that reached fewer than all four planes.</summary>
    public long MaskedPlanes { get; private set; }

    /// <summary>Writes that took a byte from the set/reset register.</summary>
    public long SetResetWrites { get; private set; }

    /// <summary>Reads that loaded the latches, which is all of them in a planar mode.</summary>
    public long Latched { get; private set; }

    /// <summary>Writes that went straight to memory, in mode 13h.</summary>
    public long LinearWrites { get; private set; }

    /// <summary>A one-line account of what the card was asked to do.</summary>
    public string Exercised()
    {
        var modes = string.Join(", ",
            Enumerable.Range(0, 4)
                .Where(i => WriteModeUses[i] > 0)
                .Select(i => $"mode {i} x{WriteModeUses[i]:N0}"));

        var functions = string.Join(", ",
            Enumerable.Range(0, 4)
                .Where(i => FunctionUses[i] > 0)
                .Select(i => $"{new[] { "replace", "and", "or", "xor" }[i]} x{FunctionUses[i]:N0}"));

        if (Linear)
        {
            return $"mode 13h, linear: {LinearWrites:N0} writes, no planes and no latches";
        }

        return $"write {modes}; {functions}; "
               + $"bit-masked x{MaskedWrites:N0}; plane-masked x{MaskedPlanes:N0}; "
               + $"set/reset x{SetResetWrites:N0}; latched x{Latched:N0}";
    }

    /// <summary>A write to the aperture, through whichever mode is set.</summary>
    public void WriteAperture(int offset, byte value)
    {
        if (Linear)
        {
            Planes[0][offset] = value;
            LinearWrites++;
            return;
        }

        Span<byte> planes = stackalloc byte[4];

        WriteModeUses[WriteMode]++;
        if (BitMask != 0xFF) MaskedWrites++;
        if (MapMask != 0x0F) MaskedPlanes++;
        if (WriteMode != 1) FunctionUses[DataRotate >> 3 & 3]++;
        if (WriteMode == 0 && EnableSetReset != 0) SetResetWrites++;

        switch (WriteMode)
        {
            case 0:
            {
                // The CPU value, rotated right by the count in the data
                // rotate register, then substituted plane by plane wherever
                // set/reset is enabled.
                var rotate = DataRotate & 7;
                var rotated = (byte)(value >> rotate | value << (8 - rotate));

                for (var plane = 0; plane < 4; plane++)
                {
                    planes[plane] = (EnableSetReset >> plane & 1) != 0
                        ? (byte)((SetReset >> plane & 1) != 0 ? 0xFF : 0x00)
                        : rotated;
                }

                break;
            }

            case 1:
                // The latches, straight through, untouched by the function
                // select or the bit mask. This is the fast copy: read a byte
                // from one place, write it to another, and the value the CPU
                // supplied is never looked at.
                for (var plane = 0; plane < 4; plane++)
                {
                    planes[plane] = Latches[plane];
                }

                break;

            case 2:
                // One bit of the CPU value per plane, expanded to a full byte.
                // This is how a solid colour gets drawn through a bit mask.
                for (var plane = 0; plane < 4; plane++)
                {
                    planes[plane] = (byte)((value >> plane & 1) != 0 ? 0xFF : 0x00);
                }

                break;

            default:
                // Write mode 3 is a VGA addition and no EGA has it. A 1988
                // EGA game reaching it means the model is wrong about
                // something upstream, so say so rather than inventing an
                // answer.
                throw new NotSupportedException(
                    $"write mode 3 is not an EGA mode (graphics mode register {GraphicsMode:X2}h)");
        }

        for (var plane = 0; plane < 4; plane++)
        {
            if ((MapMask >> plane & 1) == 0) continue;

            var latch = Latches[plane];
            var data = planes[plane];

            if (WriteMode != 1)
            {
                // Function select: replace, AND, OR or XOR against the latch.
                data = (DataRotate >> 3 & 3) switch
                {
                    0 => data,
                    1 => (byte)(data & latch),
                    2 => (byte)(data | latch),
                    3 => (byte)(data ^ latch),
                    _ => data,
                };

                // The bit mask picks, bit by bit, between the result and the
                // latch. A zero bit is not "leave memory alone" - it is "take
                // the latch", which is only the same thing when the latches
                // were loaded from this address.
                data = (byte)(data & BitMask | latch & ~BitMask);
            }

            Planes[plane][offset] = data;
        }
    }

    /// <summary>
    /// Set one colour register, as the BIOS does - through the DAC.
    /// </summary>
    /// <remarks>
    /// Not a direct poke into the array. The BIOS writes the index to 3C8h and
    /// then three values to 3C9h, which masks each to six bits and leaves the
    /// cursor on the next entry. Writing the array directly bypassed both, so
    /// a value above 63 was stored unmasked, and a BIOS call landing between
    /// two thirds of a direct write left the earlier entry's triple to be
    /// resumed by the next data write.
    /// </remarks>
    public void SetColour(int index, byte red, byte green, byte blue)
    {
        Write(0x3C8, index & 0xFF);
        Write(0x3C9, red);
        Write(0x3C9, green);
        Write(0x3C9, blue);
    }

    public byte Read(int port)
    {
        switch (port)
        {
            // The index registers read back too, which is how a 16-bit `in`
            // on an index/data pair gets a sensible answer.
            case 0x3C4:
                return (byte)_sequencerIndex;

            case 0x3CE:
                return (byte)_graphicsIndex;

            case 0x3D4:
                return (byte)_crtcIndex;

            case 0x3C5:
                return _sequencerIndex == 2 ? MapMask : (byte)0;

            case 0x3CF:
                return _graphicsIndex switch
                {
                    0 => SetReset,
                    1 => EnableSetReset,
                    2 => ColourCompare,
                    3 => DataRotate,
                    4 => ReadMapSelect,
                    5 => GraphicsMode,
                    6 => Miscellaneous,
                    7 => ColourDontCare,
                    8 => BitMask,
                    _ => 0,
                };

            case 0x3D5:
                return Crtc[_crtcIndex & 0x1F];

            // The DAC, read three bytes at a time with the index stepping on
            // every third.
            case 0x3C9:
            {
                var value = Dac[_dacIndex * 3 + _dacChannel];
                if (++_dacChannel == 3) { _dacChannel = 0; _dacIndex = (_dacIndex + 1) & 0xFF; }
                return value;
            }

            // Input status register 1. Bit 3 is vertical retrace and bit 0 is
            // display-enable-inverted. Reading it also resets the attribute
            // controller's flip-flop back to expecting an index, which is the
            // only reason a lot of palette code touches it at all.
            case 0x3DA:
            {
                _attributeExpectsIndex = true;
                var retrace = Machine?.ReadRetrace() ?? false;
                return (byte)(retrace ? 0x09 : 0x00);
            }

            // Input status register 0. Bit 7 is the vertical retrace
            // interrupt, which nothing here raises.
            case 0x3C2:
                return 0x00;

            // The DAC state register. Bits 1-0: ELEVEN means the DAC is set up
            // for a read cycle, which is what writing the read index at 3C7h
            // does, and ZERO means a write cycle, set up by writing 3C8h.
            //
            // I had this backwards twice and argued for the wrong one in a
            // commit message the second time. An older IBM manual carries the
            // reversed wording and that is where the recollection came from;
            // IBM's October 1990 VGA reference and Video Seven's June 1988
            // manual both give read = 3 and write = 0. Two dated sources
            // against a memory is not a disagreement worth keeping.
            case 0x3C7:
                return (byte)(_dacReading ? 0x03 : 0x00);

            // The write index, which reads back as the entry the cursor is on.
            case 0x3C8:
                return (byte)_dacIndex;

            default:
                throw new NotSupportedException(
                    $"read of port {port:X3}h is not modelled - "
                    + "model the device rather than answering zero");
        }
    }

    public void Write(int port, int value)
    {
        var b = (byte)value;

        switch (port)
        {
            case 0x3C4: _sequencerIndex = b & 0x1F; return;
            case 0x3C5:
                if (_sequencerIndex == 2) MapMask = (byte)(b & 0x0F);
                return;

            case 0x3CE: _graphicsIndex = b & 0x0F; return;
            case 0x3CF:
                switch (_graphicsIndex)
                {
                    case 0: SetReset = (byte)(b & 0x0F); return;
                    case 1: EnableSetReset = (byte)(b & 0x0F); return;
                    case 2: ColourCompare = (byte)(b & 0x0F); return;
                    case 3: DataRotate = (byte)(b & 0x1F); return;
                    case 4: ReadMapSelect = (byte)(b & 3); return;
                    case 5: GraphicsMode = b; return;
                    case 6: Miscellaneous = b; return;
                    case 7: ColourDontCare = (byte)(b & 0x0F); return;
                    case 8: BitMask = b; return;
                    default: return;
                }

            // The attribute controller's one address, alternating between an
            // index and a value. Nothing but a read of 3DA resets it.
            case 0x3C0:
                if (_attributeExpectsIndex)
                {
                    _attributeIndex = b & 0x1F;
                }
                else
                {
                    Attributes[_attributeIndex] = b;
                }

                _attributeExpectsIndex = !_attributeExpectsIndex;
                return;

            // The DAC's read and write index registers. Both set the same
            // counter: which one was used only decides what the next access
            // to 3C9h does, and a program that reads after setting 3C8h is
            // doing something the hardware allows.
            case 0x3C7: _dacIndex = b; _dacChannel = 0; _dacReading = true; return;
            case 0x3C8: _dacIndex = b; _dacChannel = 0; _dacReading = false; return;

            case 0x3C9:
                Dac[_dacIndex * 3 + _dacChannel] = (byte)(b & 0x3F);
                if (++_dacChannel == 3) { _dacChannel = 0; _dacIndex = (_dacIndex + 1) & 0xFF; }
                return;

            case 0x3D4: _crtcIndex = b & 0x1F; return;
            case 0x3D5: Crtc[_crtcIndex & 0x1F] = b; return;

            // Miscellaneous output and feature control: recorded by the
            // interpreter, no effect on what is in the planes.
            case 0x3C2:
            case 0x3BA:
            case 0x3DA:
                return;

            default:
                throw new NotSupportedException(
                    $"write of {value:X2} to port {port:X3}h is not modelled");
        }
    }

    /// <summary>
    /// One plane's worth of the visible screen, from the CRTC start address.
    /// </summary>
    public byte[] Plane(int plane) =>
        Planes[plane][StartAddress..(StartAddress + PlaneLength)];

    /// <summary>
    /// The screen as one byte a pixel, which is what a comparison looks at.
    /// </summary>
    /// <remarks>
    /// Four planes contribute one bit each, plane 0 being the least
    /// significant. The result is a palette index, not a colour: turning it
    /// into a colour needs <see cref="Attributes"/> as well, and two runs with
    /// identical pixels and different attributes are two different pictures.
    /// </remarks>
    public byte[] Pixels()
    {
        // Mode 13h is already one byte a pixel, from the start address on.
        if (Linear) return Planes[0][StartAddress..(StartAddress + Width * Height)];

        var pixels = new byte[Width * Height];

        for (var i = 0; i < PlaneLength; i++)
        {
            var at = StartAddress + i;
            var p0 = Planes[0][at];
            var p1 = Planes[1][at];
            var p2 = Planes[2][at];
            var p3 = Planes[3][at];

            for (var bit = 0; bit < 8; bit++)
            {
                var shift = 7 - bit;
                pixels[i * 8 + bit] = (byte)(
                    (p0 >> shift & 1)
                    | (p1 >> shift & 1) << 1
                    | (p2 >> shift & 1) << 2
                    | (p3 >> shift & 1) << 3);
            }
        }

        return pixels;
    }

    /// <summary>
    /// The 256 colours in force, as 8-bit RGB triples.
    /// </summary>
    /// <remarks>
    /// One answer for both cards, because a comparison should not have to ask
    /// which one it is looking at. A VGA's DAC holds six bits a channel and is
    /// scaled up; an EGA's sixteen palette registers are IRGB - bit 4
    /// intensity, bits 2-0 red, green, blue - which is the decoding
    /// tools/egapal.py uses and the reason its output matches the game.
    /// </remarks>
    public byte[] Colours()
    {
        var colours = new byte[256 * 3];

        if (Linear)
        {
            for (var i = 0; i < colours.Length; i++)
            {
                colours[i] = (byte)(Dac[i] * 255 / 63);
            }

            return colours;
        }

        for (var i = 0; i < 16; i++)
        {
            var v = Attributes[i];
            var intensity = (v & 0x10) != 0 ? 85 : 0;
            var red = (byte)(((v >> 2 & 1) != 0 ? 170 : 0) + intensity);
            var green = (byte)(((v >> 1 & 1) != 0 ? 170 : 0) + intensity);
            var blue = (byte)(((v >> 0 & 1) != 0 ? 170 : 0) + intensity);

            // BROWN. Value 6 would be (170,170,0), a dark yellow, and the IBM
            // hardware halves the green to make it brown. tools/egapal.py
            // applies the correction and this did not, so an exported picture
            // differed from the repository's own decoder on 1,330 pixels of
            // the title while the pixel INDICES and the palette BYTES both
            // matched - which is why neither comparison could see it.
            // Exactly value 6, as egapal.irgb tests it - not a masked
            // comparison, because 26h must not take the correction.
            if (v == 0x06) green = 85;

            colours[i * 3] = red;
            colours[i * 3 + 1] = green;
            colours[i * 3 + 2] = blue;
        }

        return colours;
    }

    /// <summary>Wipe the planes, as a mode set does.</summary>
    public void Clear()
    {
        foreach (var plane in Planes)
        {
            Array.Clear(plane);
        }

        Array.Clear(Latches);
        MapMask = 0x0F;
        BitMask = 0xFF;
        SetReset = 0;
        EnableSetReset = 0;
        DataRotate = 0;
        GraphicsMode = 0;
        ReadMapSelect = 0;
        Array.Clear(Crtc);
    }
}
