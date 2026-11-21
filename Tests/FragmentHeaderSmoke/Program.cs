using System.Buffers.Binary;
using System.IO.Compression;
using RpfPreview.Core;

// Fragment headers: +16, +32 and +40 can all be null, and the 16-bit fields at +48 are not count/capacity pairs.
// The fixture is a hand-made fragment around a one-triangle embedded drawable; it contains no game data.
const long CandidateOffset = 4096 + 16;

var plain = MakeFragment(optionalPointers: true, fields: [1, 1, 1, 1]);
var bare = MakeFragment(optionalPointers: false, fields: [1, 1, 1, 2]);
var wide = MakeFragment(optionalPointers: true, fields: [1, 2, 3, 9]);

Check(RageModelDecoder.LooksLikeFragment(plain.System), "fragment with all header pointers set");
Check(RageModelDecoder.LooksLikeFragment(bare.System), "fragment with null +16, +32 and +40");
Check(RageModelDecoder.LooksLikeFragment(wide.System), "fragment with +50 > +48 and +52 < +54");
Check(!RageModelDecoder.LooksLikeDrawable(bare.System), "a fragment is not a drawable");

var reversed = MakeFragment(optionalPointers: true, fields: [1, 1, 9, 3]);
Check(!RageModelDecoder.LooksLikeFragment(reversed.System), "rejects +52 > +54");
var noParts = MakeFragment(optionalPointers: true, fields: [0, 0, 1, 1]);
Check(!RageModelDecoder.LooksLikeFragment(noParts.System), "rejects +48 = 0");
var badPointer = MakeFragment(optionalPointers: false, fields: [1, 1, 1, 2]);
BinaryPrimitives.WriteUInt64LittleEndian(badPointer.System.AsSpan(32), 0x12345678);
Check(!RageModelDecoder.LooksLikeFragment(badPointer.System), "rejects a non-null +32 that is not a system pointer");
var noDrawable = MakeFragment(optionalPointers: false, fields: [1, 1, 1, 2]);
BinaryPrimitives.WriteUInt64LittleEndian(noDrawable.System.AsSpan(24), 0);
Check(!RageModelDecoder.LooksLikeFragment(noDrawable.System), "rejects a null drawable pointer");

foreach (var (name, fixture) in new[] { ("plain", plain), ("bare", bare), ("wide", wide) })
{
    var model = RageModelDecoder.Decode(fixture.System, fixture.Graphics);
    Check(model is { IsFragment: true, Name: "crate" } && model.Meshes.Single().Indices.SequenceEqual([0, 1, 2]),
        $"decodes the {name} fragment");
}

var directory = Directory.CreateTempSubdirectory("rpf9-fragment-test-");
string path = Path.Combine(directory.FullName, "fragment.rpf");
try
{
    using (var file = File.Create(path))
    {
        var header = new byte[64]; "9FPR"u8.CopyTo(header); file.Write(header);
        file.Position = CandidateOffset;
        foreach (var segment in new[] { bare.System, bare.Graphics })
        {
            using (var z = new ZLibStream(file, CompressionLevel.Optimal, leaveOpen: true)) z.Write(segment);
            file.Write(new byte[(int)((-file.Position) & 15)]);
        }
    }
    var candidates = Rpf9ModelRecovery.FindCandidateOffsets(path);
    Check(candidates.SequenceEqual([CandidateOffset]), "the scanner finds the bare fragment");
    var entry = Rpf9ModelRecovery.RecoverAtOffsets(path, new FileInfo(path).Length, candidates).Single();
    Check(entry.ResourceSystemBytes == bare.System.Length, "recovery splits system and graphics");
}
finally
{
    string cache = RecoveryIndexCache.CachePath("models", path);
    if (File.Exists(cache)) File.Delete(cache);
    File.Delete(path);
    directory.Delete();
}
Console.WriteLine("Fragment header checks passed.");

static void Check(bool condition, string what)
{
    if (!condition) throw new Exception("Failed: " + what);
}

static (byte[] System, byte[] Graphics) MakeFragment(bool optionalPointers, ushort[] fields)
{
    const int Pages = 0x200, FragmentName = 0x240, Extra = 0x280, Drawable = 0x300, Shader = 0x3C0, Lod = 0x400;
    const int Models = 0x420, Part = 0x440, Geometries = 0x480, Bounds = 0x4A0, Geometry = 0x500, VertexBuffer = 0x5A0;
    const int Descriptor = 0x660, IndexBuffer = 0x680, Fvf = 0x700;
    const int PositionOffset = 96, VertexCount = 3, IndexOffset = 256, IndexLength = 48;
    byte[] system = new byte[0xA00], graphics = new byte[IndexOffset + IndexLength];
    void S(int at, int address) => BinaryPrimitives.WriteUInt64LittleEndian(system.AsSpan(at), 0x50000000UL + (uint)address);
    void G(int at, int address) => BinaryPrimitives.WriteUInt64LittleEndian(system.AsSpan(at), 0x60000000UL + (uint)address);
    void U16(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(system.AsSpan(at), value);
    void U32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    void F32(byte[] bytes, int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value);

    // Fragment header.
    S(8, Pages); S(24, Drawable); S(168, FragmentName);
    if (optionalPointers) { S(16, Extra); S(32, Extra + 0x20); S(40, Extra + 0x40); }
    for (int i = 0; i < 4; i++) U16(48 + i * 2, fields[i]);
    system[Pages + 8] = system[Pages + 9] = 1;
    "crate"u8.CopyTo(system.AsSpan(FragmentName));

    // Embedded drawable: no page map and no name of its own.
    S(Drawable + 16, Shader); S(Drawable + 80, Lod);
    F32(system, Drawable + 44, 2);
    for (int axis = 0; axis < 3; axis++)
    {
        F32(system, Drawable + 64 + axis * 4, 1);
        F32(system, Bounds + 16 + axis * 4, 1);
    }
    S(Lod, Models); U16(Lod + 8, 1); U16(Lod + 10, 1); S(Models, Part);
    S(Part + 8, Geometries); U16(Part + 16, 1); U16(Part + 18, 1); U16(Part + 54, 1); S(Part + 24, Bounds);
    S(Geometries, Geometry); S(Geometry + 8, VertexBuffer); S(Geometry + 64, Descriptor);

    // Float positions: (0,0,0), (1,0,0), (1,1,1).
    U32(system, VertexBuffer + 8, PositionOffset + VertexCount * 12); U16(VertexBuffer + 12, 1);
    G(VertexBuffer + 24, 0); S(VertexBuffer + 64, Fvf);
    U32(system, Fvf, PositionOffset);
    system[Fvf + 0xD0] = 12; system[Fvf + 0x104] = 6;
    U32(system, Fvf + 0x138, (VertexCount << 10) | 1);
    U32(graphics, 0, 0x63);
    F32(graphics, PositionOffset + 12, 1);
    F32(graphics, PositionOffset + 24, 1); F32(graphics, PositionOffset + 28, 1); F32(graphics, PositionOffset + 32, 1);

    // One cluster, one triangle, three 16-bit vertex references.
    S(Descriptor, IndexBuffer); U16(Descriptor + 8, 1); U16(Descriptor + 10, 1);
    U32(system, Descriptor + 12, VertexCount << 8);
    U32(system, IndexBuffer + 8, IndexLength / 4); U16(IndexBuffer + 12, 4); G(IndexBuffer + 24, IndexOffset);
    U32(graphics, IndexOffset, 0x80000001);
    U32(graphics, IndexOffset + 4, 1); U32(graphics, IndexOffset + 8, VertexCount);
    U32(graphics, IndexOffset + 12, VertexCount - 1);
    U32(graphics, IndexOffset + 28, (2u << 14) | (1u << 7));
    graphics[IndexOffset + 34] = 1; graphics[IndexOffset + 36] = 2;
    return (system, graphics);
}
