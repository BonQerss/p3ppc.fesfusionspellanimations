using System.Buffers.Binary;
using System.Text;

namespace p3ppc.fesfusionspellanimations
{
    // P3P model packs store [name 252][u32 size][data padded to 64] per file and end with a blank 256 byte header
    internal static class Pac
    {
        private const int NameLength = 252;

        public static List<(string Name, byte[] Data)> Read(string path) => Read(File.ReadAllBytes(path));

        public static List<(string Name, byte[] Data)> Read(byte[] d)
        {
            var entries = new List<(string, byte[])>();
            int offset = 0;
            while (offset + 256 <= d.Length)
            {
                int nameEnd = Array.IndexOf(d, (byte)0, offset, NameLength);
                string name = Encoding.ASCII.GetString(d, offset, (nameEnd < 0 ? offset + NameLength : nameEnd) - offset);
                if (name.Length == 0)
                    break;

                int size = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(offset + NameLength));
                if (size < 0 || offset + 256 + size > d.Length)
                    throw new InvalidDataException($"Broken pack entry {name}.");

                entries.Add((name, d[(offset + 256)..(offset + 256 + size)]));
                offset += 256 + ((size + 63) & ~63);
            }
            return entries;
        }

        public static void Write(string path, IEnumerable<(string Name, byte[] Data)> entries)
        {
            string temp = path + ".tmp";
            using (var stream = File.Create(temp))
            {
                Span<byte> header = stackalloc byte[256];
                foreach (var (name, data) in entries)
                {
                    header.Clear();
                    Encoding.ASCII.GetBytes(name, header);
                    BinaryPrimitives.WriteInt32LittleEndian(header[NameLength..], data.Length);
                    stream.Write(header);
                    stream.Write(data);
                    int pad = ((data.Length + 63) & ~63) - data.Length;
                    if (pad > 0)
                        stream.Write(new byte[pad]);
                }

                // The game walks entries until a blank header, without it it reads past the file
                header.Clear();
                stream.Write(header);
            }
            File.Move(temp, path, true);
        }
    }
}
