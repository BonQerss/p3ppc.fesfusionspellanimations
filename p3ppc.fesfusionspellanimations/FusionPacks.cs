using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using CriFs.V2.Hook.Interfaces;

namespace p3ppc.fesfusionspellanimations
{
    // Adds the FES fusion motions as slots 24+ to every protagonist pack the game will load,
    // whichever mod (or the base game) supplies it
    internal sealed class FusionPacks
    {
        private const int Version = 1;
        private const int VanillaMotions = 24;
        private const ushort Translate = 0x48;
        private static readonly Regex PackName = new(@"^(bc001|bc063)(_[a-z0-9]+)*\.pac$", RegexOptions.IgnoreCase);
        private static readonly string[] RootBones = { "Bip01_Bone", "Bip01 Pelvis_Bone", "Bip01 Footsteps_Bone" };

        private readonly ICriFsRedirectorApi _criFs;
        private readonly string _cacheDirectory;
        private readonly Dictionary<string, GmoFile> _motions = new();
        private readonly Action<string> _log;

        public FusionPacks(ICriFsRedirectorApi criFs, string modDirectory, Action<string> log)
        {
            _criFs = criFs;
            _log = log;
            _cacheDirectory = Path.Combine(modDirectory, "Cache");
            Directory.CreateDirectory(_cacheDirectory);
            foreach (string model in new[] { "bc001", "bc063" })
            {
                byte[] data = File.ReadAllBytes(Path.Combine(modDirectory, "Motions", model + ".GMO"));
                _motions[model] = GmoFile.Load(data);
                _motionStamp += $"{data.Length}.{Hash(Convert.ToBase64String(data))}|";
            }
        }

        private readonly string _motionStamp = "";

        public void OnBind(ICriFsRedirectorApi.BindContext bind)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int count = 0;

            foreach (var (key, files) in bind.RelativePathToFileMap.ToList())
            {
                if (!IsProtagonistPack(key) || files.Count == 0)
                    continue;

                var source = files[^1];
                var info = new FileInfo(source.FullPath);
                string? merged = Merge(key, $"{source.FullPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}", () => File.ReadAllBytes(source.FullPath));
                if (merged == null)
                    continue;

                files.Add(new ICriFsRedirectorApi.BindFileInfo { FullPath = merged, ModId = "p3ppc.fesfusionspellanimations", LastWriteTime = DateTime.UtcNow });
                used.Add(merged);
                count++;
            }

            // Packs no mod replaces still come from the game's CPKs
            var lib = _criFs.GetCriFsLib();
            foreach (string cpk in _criFs.GetCpkFilesInGameDir())
            {
                var cached = _criFs.GetCpkFilesCached(cpk);
                foreach (var file in cached.Files)
                {
                    string key = Path.Combine("R2", file.FullPath.Replace('/', '\\'));
                    if (!IsProtagonistPack(key) || bind.RelativePathToFileMap.ContainsKey(key))
                        continue;

                    var entry = file.File;
                    string? merged = Merge(key, $"{cpk}|{entry.FileOffset}|{entry.FileSize}|{cached.LastModified.Ticks}", () =>
                    {
                        using var stream = new FileStream(cpk, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var reader = lib.CreateCpkReader(stream, false);
                        using var data = reader.ExtractFile(entry);
                        return data.Span.ToArray();
                    });
                    if (merged == null)
                        continue;

                    bind.RelativePathToFileMap[key] = new List<ICriFsRedirectorApi.BindFileInfo>
                    {
                        new() { FullPath = merged, ModId = "p3ppc.fesfusionspellanimations", LastWriteTime = DateTime.UtcNow }
                    };
                    used.Add(merged);
                    count++;
                }
            }

            foreach (string file in Directory.EnumerateFiles(_cacheDirectory))
            {
                if (!used.Contains(file))
                    File.Delete(file);
            }
            _log($"Fusion animations added to {count} packs.");
        }

        private static bool IsProtagonistPack(string key) => PackName.IsMatch(Path.GetFileName(key)) && !key.Contains("_dm", StringComparison.OrdinalIgnoreCase)
            && key.Replace('/', '\\').Contains(@"model\pack\", StringComparison.OrdinalIgnoreCase);

        private string? Merge(string key, string stamp, Func<byte[]> read)
        {
            string name = Path.GetFileNameWithoutExtension(key);
            string path = Path.Combine(_cacheDirectory, $"{name}_{Hash($"{Version}|{_motionStamp}{stamp}")}.pac");
            if (File.Exists(path))
                return path;

            try
            {
                var motions = _motions[name[..5].ToLowerInvariant()];
                bool changed = false;
                var entries = Pac.Read(read()).Select(e =>
                {
                    if (!e.Name.EndsWith(".GMO", StringComparison.OrdinalIgnoreCase))
                        return e;
                    var gmo = AddMotions(e.Data, motions);
                    changed |= gmo != null;
                    return (e.Name, gmo ?? e.Data);
                }).ToList();

                if (!changed)
                    return null;
                Pac.Write(path, entries);
                return path;
            }
            catch (Exception e)
            {
                _log($"Couldn't add fusion animations to {key}: {e.Message}");
                return null;
            }
        }

        public static byte[]? AddMotions(byte[] data, GmoFile fusion)
        {
            var target = GmoFile.Load(data);
            var model = target.GetModel();
            var motions = model.Find(GmoFile.Motion).ToList();
            var bones = model.Find(GmoFile.Bone).Select(b => Key(Name(b))).ToList();
            if (motions.Count != VanillaMotions || !bones.Contains(Key("Bip01 Head_Bone")))
                return null;

            var fusionModel = fusion.GetModel();
            var fusionBones = fusionModel.Find(GmoFile.Bone).Select(Name).ToList();

            // Evoker and blink tracks come from the model's own summon (13) and fusion (21) motions
            int insertAt = model.Children!.IndexOf(motions[^1]) + 1;
            var added = fusionModel.Find(GmoFile.Motion).Select((m, i) => Retarget(m.Clone(), fusionBones, bones, motions[i == 0 ? 13 : 21])).ToList();
            model.Children.InsertRange(insertAt, added);
            return target.Save();
        }

        // Bone tracks move by bone name. Only root bones keep their translation so other skeletons keep their own proportions.
        private static GmoChunk Retarget(GmoChunk motion, List<string> fusionBones, List<string> bones, GmoChunk own)
        {
            var curves = motion.Find(GmoFile.FCurve).ToList();
            var keptCurves = new List<GmoChunk>();
            var animates = new List<GmoChunk>();

            void Add(GmoChunk animate, GmoChunk curve, int type, int index)
            {
                var copy = animate.Clone();
                BinaryPrimitives.WriteUInt32LittleEndian(copy.Data.AsSpan(8), ((uint)type << 16) | 0x1000u | (uint)index);
                BinaryPrimitives.WriteUInt16LittleEndian(copy.Data.AsSpan(20), (ushort)keptCurves.Count);
                animates.Add(copy);
                keptCurves.Add(curve.Clone());
            }

            foreach (var (animate, type, index, command, curve) in Animates(motion))
            {
                if (type != GmoFile.Bone || index >= fusionBones.Count || curve >= curves.Count)
                    continue;
                int bone = bones.IndexOf(Key(fusionBones[index]));
                if (bone >= 0 && (command != Translate || RootBones.Contains(fusionBones[index])))
                    Add(animate, curves[curve], type, bone);
            }

            var ownCurves = own.Find(GmoFile.FCurve).ToList();
            foreach (var (animate, type, index, _, curve) in Animates(own))
            {
                if (type == GmoFile.Material && curve < ownCurves.Count)
                    Add(animate, ownCurves[curve], type, index);
            }

            var header = motion.Children!.Where(c => c.Type != GmoFile.Animate && c.Type != GmoFile.FCurve);
            motion.Children = header.Concat(animates).Concat(keptCurves).ToList();
            return motion;
        }

        private static IEnumerable<(GmoChunk Animate, int Type, int Index, int Command, int Curve)> Animates(GmoChunk motion)
        {
            foreach (var child in motion.Find(GmoFile.Animate))
            {
                var d = child.Data!;
                uint block = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(8));
                yield return (child, (int)(block >> 16), (int)(block & 0xFFF), BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(12)), BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(20)));
            }
        }

        private static string Name(GmoChunk chunk) => ReadString(chunk.Head!, 16, chunk.Head!.Length);

        private static string ReadString(byte[] d, int start, int end)
        {
            if (end <= start)
                return "";
            int zero = Array.IndexOf(d, (byte)0, start, end - start);
            return Encoding.ASCII.GetString(d, start, (zero < 0 ? end : zero) - start);
        }

        private static string Key(string name) => name.Replace(" ", "").Replace("_", "").ToLowerInvariant();

        private static string Hash(string text)
        {
            ulong h = 14695981039346656037;
            foreach (char c in text)
                h = (h ^ c) * 1099511628211;
            return h.ToString("x16");
        }
    }
}
