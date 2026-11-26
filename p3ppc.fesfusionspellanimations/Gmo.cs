using System.Buffers.Binary;
using System.Text;

namespace p3ppc.fesfusionspellanimations
{
    internal sealed class GmoChunk
    {
        public ushort Type;
        public byte[]? Head;
        public List<GmoChunk>? Children;
        public byte[]? Data;

        public IEnumerable<GmoChunk> Find(ushort type) => Children?.Where(c => c.Type == type) ?? Enumerable.Empty<GmoChunk>();

        public GmoChunk Clone() => new()
        {
            Type = Type,
            Head = Head?.ToArray(),
            Data = Data?.ToArray(),
            Children = Children?.Select(c => c.Clone()).ToList(),
        };
    }

    internal sealed class GmoFile
    {
        public const ushort Model = 0x0003;
        public const ushort Bone = 0x0004;
        public const ushort Material = 0x0008;
        public const ushort Motion = 0x000B;
        public const ushort FCurve = 0x000C;
        public const ushort Animate = 0x80B3;
        public const ushort Helper = 0x80F1;
        private const ushort Arrays = 0x0007;
        private const ushort Tail = 0xFFFF;

        private readonly byte[] _header;
        public readonly List<GmoChunk> Chunks;

        private GmoFile(byte[] header, List<GmoChunk> chunks)
        {
            _header = header;
            Chunks = chunks;
        }

        public static GmoFile Load(byte[] data)
        {
            if (data.Length < 0x10 || data[0] != 'O' || data[1] != 'M' || data[2] != 'G')
                throw new InvalidDataException("Not a GMO file.");
            return new GmoFile(data[..0x10], Parse(data, 0x10, data.Length));
        }

        private static List<GmoChunk> Parse(byte[] d, int offset, int end)
        {
            var list = new List<GmoChunk>();
            while (offset + 8 <= end)
            {
                ushort type = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(offset));
                ushort headSize = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(offset + 2));
                int size = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(offset + 4));
                if (size < 8 || offset + size > end)
                    break;

                if ((type & 0x8000) != 0 || type == Arrays || type == FCurve)
                {
                    list.Add(new GmoChunk { Type = type, Data = d[offset..(offset + size)] });
                }
                else
                {
                    int head = Math.Max((int)headSize, 8);
                    list.Add(new GmoChunk { Type = type, Head = d[offset..(offset + head)], Children = Parse(d, offset + head, offset + size) });
                }
                offset += size;
            }

            if (offset < end)
                list.Add(new GmoChunk { Type = Tail, Data = d[offset..end] });
            return list;
        }

        public byte[] Save()
        {
            using var stream = new MemoryStream();
            stream.Write(_header);
            foreach (var chunk in Chunks)
                Write(stream, chunk);
            return stream.ToArray();
        }

        private static void Write(MemoryStream stream, GmoChunk chunk)
        {
            if (chunk.Children == null)
            {
                stream.Write(chunk.Data);
                return;
            }

            long start = stream.Position;
            stream.Write(chunk.Head);
            foreach (var child in chunk.Children)
                Write(stream, child);

            long end = stream.Position;
            stream.Position = start + 4;
            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(size, (int)(end - start));
            stream.Write(size);
            stream.Position = end;
        }

        public GmoChunk GetModel() => Chunks[0].Find(Model).First();
    }
}
