using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using RpfPreview.Core;

// Drawable dictionaries: a dictionary header whose values are embedded drawables, keyed by the hashes of their
// names in ascending order. The fixture is hand-made (two one-triangle drawables) and contains no game data.
const long CandidateOffset = 4096 + 16;
string[] names = ["tile_b", "tile_a"];
uint[] keys = names.Select(Hash).ToArray();
// Dictionary order is ascending by key.
var order = Enumerable.Range(0, names.Length).OrderBy(i => keys[i]).ToArray();
string[] sortedNames = order.Select(i => names[i]).ToArray();

var good = MakeDictionary(sortedNames, sortedNames.Select(Hash).ToArray());
Check(RageModelDecoder.LooksLikeDrawableDictionary(good.System), "dictionary header");
Check(RageModelDecoder.LooksLikeResource(good.System), "dictionary is a model resource");
Check(!RageModelDecoder.LooksLikeDrawable(good.System) && !RageModelDecoder.LooksLikeFragment(good.System), "not a drawable or fragment");

var model = RageModelDecoder.Decode(good.System, good.Graphics);
Check(model is not null, "decodes the dictionary");
var entries = model!.DictionaryEntries!;
Check(entries.Count == 2 && entries.Select(e => e.Name).SequenceEqual(sortedNames.Select(n => n + ".#dr")), "entry names in dictionary order");
Check(entries.Select(e => (e.FirstMesh, e.MeshCount)).SequenceEqual([(0, 1), (1, 1)]), "entry mesh ranges");
Check(model.Meshes.Select(m => m.Name).SequenceEqual(sortedNames.Select(n => n + "/model_0_geometry_0")), "mesh names carry the drawable name");
Check(model.Meshes.All(m => m.Indices.SequenceEqual([0, 1, 2])), "both triangles decode");
Check(entries[1].BoundsMax.X == 2 && model.BoundsMax.X == 2 && model.Name == sortedNames[0], "entry and dictionary bounds");
var extent = RageModelDecoder.ProbeRequiredExtents(good.System);
Check(extent is { MetadataComplete: true } && extent.RequiredGraphicsBytes == 256 + 160 + 48, "extent ends at the second drawable's index data");

var wrongKey = MakeDictionary(sortedNames, [keys[order[0]], keys[order[1]] ^ 1]);
Check(RageModelDecoder.Decode(wrongKey.System, wrongKey.Graphics) is null, "rejects a key that is not the name hash");
var unsorted = MakeDictionary(sortedNames.Reverse().ToArray(), sortedNames.Reverse().Select(Hash).ToArray());
Check(RageModelDecoder.Decode(unsorted.System, unsorted.Graphics) is null, "rejects keys out of order");
var notDrawable = MakeDictionary(sortedNames, sortedNames.Select(Hash).ToArray());
BinaryPrimitives.WriteUInt64LittleEndian(notDrawable.System.AsSpan(notDrawable.FirstDrawable + 8), 0x50000200); // a page map: not embedded
Check(RageModelDecoder.Decode(notDrawable.System, notDrawable.Graphics) is null, "rejects entries that are not embedded drawables");
var wrongCount = MakeDictionary(sortedNames, sortedNames.Select(Hash).ToArray());
BinaryPrimitives.WriteUInt16LittleEndian(wrongCount.System.AsSpan(58), 3);
Check(!RageModelDecoder.LooksLikeDrawableDictionary(wrongCount.System), "rejects mismatched count/capacity");

var directory = Directory.CreateTempSubdirectory("rpf9-dictionary-test-");
string path = Path.Combine(directory.FullName, "dictionary.rpf");
try
{
    using (var file = File.Create(path))
    {
        var header = new byte[64]; "9FPR"u8.CopyTo(header); file.Write(header);
        file.Position = CandidateOffset;
        foreach (var segment in new[] { good.System, good.Graphics })
        {
            using (var z = new ZLibStream(file, CompressionLevel.Optimal, leaveOpen: true)) z.Write(segment);
            file.Write(new byte[(int)((-file.Position) & 15)]);
        }
    }
    var candidates = Rpf9ModelRecovery.FindCandidateOffsets(path);
    Check(candidates.SequenceEqual([CandidateOffset]), "the scanner finds the dictionary");
    var entry = Rpf9ModelRecovery.RecoverAtOffsets(path, new FileInfo(path).Length, candidates).Single();
    Check(entry.ResourceSystemBytes == good.System.Length && entry.Path.Contains(sortedNames[0]), "recovery splits and names the dictionary");
}
finally
{
    string cache = RecoveryIndexCache.CachePath("models", path);
    if (File.Exists(cache)) File.Delete(cache);
    File.Delete(path);
    directory.Delete();
}
Console.WriteLine("Drawable dictionary checks passed.");

static void Check(bool condition, string what)
{
    if (!condition) throw new Exception("Failed: " + what);
}

static uint Hash(string name)
{
    uint h = 0;
    foreach (byte c in Encoding.UTF8.GetBytes(name.ToLowerInvariant())) { h += c; h += h << 10; h ^= h >> 6; }
    h += h << 3; h ^= h >> 11; h += h << 15;
    return h;
}

static (byte[] System, byte[] Graphics, int FirstDrawable) MakeDictionary(string[] names, uint[] keys)
{
    const int Pages = 0x100, Hashes = 0x140, Values = 0x180, Base = 0x400, Stride = 0x800;
    const int PositionOffset = 96, VertexCount = 3, IndexLength = 48, GraphicsStride = 256;
    int n = names.Length;
    byte[] system = new byte[Base + n * Stride], graphics = new byte[n * GraphicsStride];
    void S(int at, int address) => BinaryPrimitives.WriteUInt64LittleEndian(system.AsSpan(at), 0x50000000UL + (uint)address);
    void G(int at, int address) => BinaryPrimitives.WriteUInt64LittleEndian(system.AsSpan(at), 0x60000000UL + (uint)address);
    void U16(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(system.AsSpan(at), value);
    void U32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    void F32(byte[] bytes, int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value);

    // Dictionary header: page map, no parent, one reference, keys and values with count/capacity pairs.
    S(8, Pages); BinaryPrimitives.WriteUInt64LittleEndian(system.AsSpan(24), 1);
    S(32, Hashes); U16(40, (ushort)n); U16(42, (ushort)n);
    S(48, Values); U16(56, (ushort)n); U16(58, (ushort)n);
    system[Pages + 8] = system[Pages + 9] = 1;
    for (int k = 0; k < n; k++)
    {
        int d = Base + k * Stride, g = k * GraphicsStride;
        int name = d + 0x180, shader = d + 0x1C0, lod = d + 0x200, models = d + 0x220, part = d + 0x240, geometries = d + 0x280;
        int bounds = d + 0x2A0, geometry = d + 0x300, vb = d + 0x3A0, descriptor = d + 0x460, ib = d + 0x480, fvf = d + 0x500;
        U32(system, Hashes + k * 4, keys[k]); S(Values + k * 8, d);
        Encoding.ASCII.GetBytes(names[k] + ".#dr").CopyTo(system, name);
        // Embedded drawable: no page map of its own. Entry k spans x in [0, k + 1].
        S(d + 16, shader); S(d + 80, lod); S(d + 168, name);
        F32(system, d + 44, 2);
        F32(system, d + 64, k + 1); F32(system, d + 68, 1); F32(system, d + 72, 1);
        F32(system, bounds + 16, k + 1); F32(system, bounds + 20, 1); F32(system, bounds + 24, 1);
        S(lod, models); U16(lod + 8, 1); U16(lod + 10, 1); S(models, part);
        S(part + 8, geometries); U16(part + 16, 1); U16(part + 18, 1); U16(part + 54, 1); S(part + 24, bounds);
        S(geometries, geometry); S(geometry + 8, vb); S(geometry + 64, descriptor);
        U32(system, vb + 8, PositionOffset + VertexCount * 12); U16(vb + 12, 1); G(vb + 24, g); S(vb + 64, fvf);
        U32(system, fvf, PositionOffset);
        system[fvf + 0xD0] = 12; system[fvf + 0x104] = 6;
        U32(system, fvf + 0x138, (VertexCount << 10) | 1);
        U32(graphics, g, 0x63);
        F32(graphics, g + PositionOffset + 12, k + 1);
        F32(graphics, g + PositionOffset + 24, k + 1); F32(graphics, g + PositionOffset + 28, 1); F32(graphics, g + PositionOffset + 32, 1);
        int index = g + 160;
        S(descriptor, ib); U16(descriptor + 8, 1); U16(descriptor + 10, 1);
        U32(system, descriptor + 12, VertexCount << 8);
        U32(system, ib + 8, IndexLength / 4); U16(ib + 12, 4); G(ib + 24, index);
        U32(graphics, index, 0x80000001);
        U32(graphics, index + 4, 1); U32(graphics, index + 8, VertexCount);
        U32(graphics, index + 12, VertexCount - 1);
        U32(graphics, index + 28, (2u << 14) | (1u << 7));
        graphics[index + 34] = 1; graphics[index + 36] = 2;
    }
    return (system, graphics, Base);
}
