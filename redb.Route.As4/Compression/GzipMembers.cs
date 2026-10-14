using System.Buffers.Binary;
using Org.BouncyCastle.Utilities.Zlib;

namespace redb.Route.As4.Compression;

/// <summary>
/// Walks a gzip stream member by member (RFC 1952) and says whether it is whole: every member has a well-formed
/// header, a deflate stream that reaches its end, and a trailer whose CRC-32 and length are those of what it inflated;
/// nothing follows the last member.
/// <para>
/// <c>GZipStream</c> inflates a stream of any number of members but ends without an exception when its input stops in
/// the middle of one, and it does not say where a member ends. This reader does: it inflates with an inflater that
/// reports how much input it consumed, so the trailer of each member is found and checked.
/// </para>
/// </summary>
internal static class GzipMembers
{
    private const int FlagHeaderCrc = 0x02;
    private const int FlagExtra = 0x04;
    private const int FlagName = 0x08;
    private const int FlagComment = 0x10;
    private const int FlagReserved = 0xE0;

    /// <summary>
    /// Whether <paramref name="compressed"/>, read from its start, is one or more whole gzip members that inflate to
    /// exactly <paramref name="expectedLength"/> bytes in total.
    /// </summary>
    public static bool AreWhole(Stream compressed, long expectedLength)
    {
        ArgumentNullException.ThrowIfNull(compressed);
        compressed.Position = 0;
        var input = new Input(compressed);
        var window = new byte[81920];
        long total = 0;
        var members = 0;
        Span<byte> trailer = stackalloc byte[8];

        while (input.Fill())
        {
            if (!ReadHeader(input))
                return false;

            var inflater = new ZStream();
            if (inflater.inflateInit(true) != JZlib.Z_OK)   // nowrap: a raw deflate stream, as gzip carries it
                return false;
            var crc = 0xFFFFFFFFu;
            long length = 0;
            try
            {
                while (true)
                {
                    if (input.Available == 0 && !input.Fill())
                        return false;                        // the input ends inside the deflate stream

                    inflater.next_in = input.Buffer;
                    inflater.next_in_index = input.Position;
                    inflater.avail_in = input.Available;
                    inflater.next_out = window;
                    inflater.next_out_index = 0;
                    inflater.avail_out = window.Length;

                    var result = inflater.inflate(JZlib.Z_NO_FLUSH);
                    var consumed = inflater.next_in_index - input.Position;
                    var produced = inflater.next_out_index;
                    input.Position = inflater.next_in_index;
                    crc = Crc32.Append(crc, window.AsSpan(0, produced));
                    length += produced;
                    if (total + length > expectedLength)
                        return false;

                    if (result == JZlib.Z_STREAM_END)
                        break;
                    if (result != JZlib.Z_OK || (consumed == 0 && produced == 0))
                        return false;                        // not deflate, or no progress with input and room at hand
                }
            }
            finally
            {
                inflater.inflateEnd();
            }

            if (!input.Read(trailer)
                || BinaryPrimitives.ReadUInt32LittleEndian(trailer) != ~crc
                || BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]) != unchecked((uint)length))
                return false;

            total += length;
            members++;
        }

        return members > 0 && total == expectedLength;
    }

    /// <summary>Reads one member header (RFC 1952 §2.3); false when it is not one or the input ends inside it.</summary>
    private static bool ReadHeader(Input input)
    {
        Span<byte> fixedPart = stackalloc byte[10];
        if (!input.Read(fixedPart) || fixedPart[0] != 0x1F || fixedPart[1] != 0x8B || fixedPart[2] != 8)
            return false;
        var flags = fixedPart[3];
        if ((flags & FlagReserved) != 0)
            return false;

        if ((flags & FlagExtra) != 0)
        {
            Span<byte> size = stackalloc byte[2];
            if (!input.Read(size) || !input.Skip(BinaryPrimitives.ReadUInt16LittleEndian(size)))
                return false;
        }
        if ((flags & FlagName) != 0 && !input.SkipPastZero())
            return false;
        if ((flags & FlagComment) != 0 && !input.SkipPastZero())
            return false;
        if ((flags & FlagHeaderCrc) != 0 && !input.Skip(2))
            return false;
        return true;
    }

    /// <summary>A buffer over the compressed stream that the inflater and the header and trailer readers share.</summary>
    private sealed class Input(Stream source)
    {
        public byte[] Buffer { get; } = new byte[81920];
        public int Position { get; set; }
        private int _end;

        public int Available => _end - Position;

        /// <summary>Makes at least one byte available; false at the end of the stream.</summary>
        public bool Fill()
        {
            if (Available > 0)
                return true;
            Position = 0;
            _end = source.Read(Buffer, 0, Buffer.Length);
            return _end > 0;
        }

        public bool Read(Span<byte> destination)
        {
            for (var i = 0; i < destination.Length; i++)
            {
                if (!Fill()) return false;
                destination[i] = Buffer[Position++];
            }
            return true;
        }

        public bool Skip(int count)
        {
            while (count > 0)
            {
                if (!Fill()) return false;
                var step = Math.Min(count, Available);
                Position += step;
                count -= step;
            }
            return true;
        }

        public bool SkipPastZero()
        {
            while (Fill())
                if (Buffer[Position++] == 0)
                    return true;
            return false;
        }
    }
}

/// <summary>CRC-32 of gzip (ISO 3309, reflected polynomial 0xEDB88320).</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }

    /// <summary>Continues the running value <paramref name="crc"/> (start with 0xFFFFFFFF, finish with its complement).</summary>
    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
