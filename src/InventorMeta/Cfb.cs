using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace InventorMeta;

/// <summary>
/// Self-contained reader for the OLE Compound File Binary (CFB / "Structured
/// Storage") format - the container Autodesk Inventor uses for .ipt/.iam/.idw/.ipn.
/// Implements enough of [MS-CFB] to enumerate the storage tree and read any stream.
/// No COM, no external dependencies - pure managed byte parsing.
/// </summary>
public sealed class CompoundFile : IDisposable
{
    public const uint FREESECT   = 0xFFFFFFFF;
    public const uint ENDOFCHAIN = 0xFFFFFFFE;
    public const uint NOSTREAM   = 0xFFFFFFFF;

    private readonly byte[] _data;
    public int  SectorSize { get; }
    public int  MiniSectorSize { get; }
    public int  MajorVersion { get; }
    public int  MiniStreamCutoff { get; }
    public uint[] Fat { get; }
    public uint[] MiniFat { get; }
    public List<DirEntry> Directory { get; } = [];
    private byte[] _miniStream = [];

    public sealed class DirEntry
    {
        public string Name = "";
        public byte   Type;            // 0 invalid, 1 storage, 2 stream, 5 root
        public Guid   Clsid;
        public uint   Left = NOSTREAM, Right = NOSTREAM, Child = NOSTREAM;
        public uint   StartSector;
        public long   Size;
        public uint   StateBits;
        public DateTime? Created, Modified;
        public int    Index;
        // Filled during tree walk:
        public string Path = "";
        public int    Depth;
        public string TypeName => Type switch { 1 => "storage", 2 => "stream", 5 => "root", _ => "free" };
    }

    public static bool LooksLikeCompoundFile(string path)
    {
        using FileStream fs = File.OpenRead(path);
        Span<byte> sig = stackalloc byte[8];
        return fs.Read(sig) == 8 &&
               sig[0]==0xD0 && sig[1]==0xCF && sig[2]==0x11 && sig[3]==0xE0 &&
               sig[4]==0xA1 && sig[5]==0xB1 && sig[6]==0x1A && sig[7]==0xE1;
    }

    public CompoundFile(string path)
    {
        _data = File.ReadAllBytes(path);
        if (_data.Length < 512)
        {
            throw new InvalidDataException("File too small to be a compound file.");
        }

        // ---- Header ----
        for (int i = 0; i < 8; i++)
        {
            if (_data[i] != new byte[]{0xD0,0xCF,0x11,0xE0,0xA1,0xB1,0x1A,0xE1}[i])
            {
                throw new InvalidDataException("Not an OLE compound file (bad signature).");
            }
        }

        MajorVersion       = U16(26);
        int sectorShift    = U16(30);
        int miniSectorShift= U16(32);
        SectorSize         = 1 << sectorShift;       // 512 (v3) or 4096 (v4)
        MiniSectorSize     = 1 << miniSectorShift;   // 64
        _ = (int)U32(44);
        uint firstDirSect  = U32(48);
        MiniStreamCutoff   = (int)U32(56);
        uint firstMiniFat  = U32(60);
        int numMiniFat     = (int)U32(64);
        uint firstDifat    = U32(68);
        int numDifat       = (int)U32(72);

        // ---- Assemble DIFAT (list of FAT sector locations) ----
        List<uint> fatSectors = [];
        for (int i = 0; i < 109; i++)
        {
            uint v = U32(76 + i * 4);
            if (v == FREESECT || v == ENDOFCHAIN)
            {
                break;
            }

            fatSectors.Add(v);
        }
        uint difatSect = firstDifat;
        int difatGuard = 0;
        while (difatSect != ENDOFCHAIN && difatSect != FREESECT && difatGuard++ < numDifat + 4)
        {
            int baseOff = SectorOffset(difatSect);
            int entriesPerDifat = SectorSize / 4 - 1;
            for (int i = 0; i < entriesPerDifat; i++)
            {
                uint v = U32(baseOff + i * 4);
                if (v == FREESECT || v == ENDOFCHAIN)
                {
                    continue;
                }

                fatSectors.Add(v);
            }
            difatSect = U32(baseOff + entriesPerDifat * 4);
        }

        // ---- Read FAT ----
        int fatEntries = fatSectors.Count * (SectorSize / 4);
        Fat = new uint[fatEntries];
        int fi = 0;
        foreach (uint s in fatSectors)
        {
            int off = SectorOffset(s);
            for (int i = 0; i < SectorSize / 4; i++)
            {
                Fat[fi++] = U32(off + i * 4);
            }
        }

        // ---- Read MiniFAT ----
        List<uint> miniFat = [];
        uint ms = firstMiniFat; int mGuard = 0;
        while (ms != ENDOFCHAIN && ms != FREESECT && mGuard++ < numMiniFat + 8)
        {
            int off = SectorOffset(ms);
            for (int i = 0; i < SectorSize / 4; i++)
            {
                miniFat.Add(U32(off + i * 4));
            }

            ms = NextFat(ms);
        }
        MiniFat = miniFat.ToArray();

        // ---- Read directory entries ----
        byte[] dirBytes = ReadFatChain(firstDirSect);
        int count = dirBytes.Length / 128;
        for (int i = 0; i < count; i++)
        {
            int o = i * 128;
            int nameLen = BitConverter.ToUInt16(dirBytes, o + 64);
            string name = nameLen > 2 ? Encoding.Unicode.GetString(dirBytes, o, nameLen - 2) : "";
            DirEntry e = new()
            {
                Index       = i,
                Name        = name,
                Type        = dirBytes[o + 66],
                Left        = BitConverter.ToUInt32(dirBytes, o + 68),
                Right       = BitConverter.ToUInt32(dirBytes, o + 72),
                Child       = BitConverter.ToUInt32(dirBytes, o + 76),
                Clsid       = new Guid(new ReadOnlySpan<byte>(dirBytes, o + 80, 16)),
                StateBits   = BitConverter.ToUInt32(dirBytes, o + 96),
                StartSector = BitConverter.ToUInt32(dirBytes, o + 116),
                Size        = BitConverter.ToInt64(dirBytes, o + 120),
                Created     = ToDate(BitConverter.ToInt64(dirBytes, o + 100)),
                Modified    = ToDate(BitConverter.ToInt64(dirBytes, o + 108)),
            };
            if (MajorVersion == 3)
            {
                e.Size &= 0xFFFFFFFF; // high dword reserved in v3
            }

            Directory.Add(e);
        }

        // ---- Mini stream container = root entry's chain in the main FAT ----
        if (Directory.Count > 0 && Directory[0].Type == 5)
        {
            _miniStream = ReadFatChain(Directory[0].StartSector, Directory[0].Size);
        }

        // ---- Build human-readable paths via red-black-tree walk ----
        if (Directory.Count > 0)
        {
            WalkTree(Directory[0].Child, "", 0);
        }

        Directory[0].Path = "/";
        Directory[0].Depth = 0;
    }

    private void WalkTree(uint id, string parentPath, int depth)
    {
        if (id == NOSTREAM || id >= Directory.Count)
        {
            return;
        }

        DirEntry e = Directory[(int)id];
        WalkTree(e.Left, parentPath, depth);
        e.Path = parentPath + "/" + e.Name;
        e.Depth = depth;
        if (e.Type == 1 && e.Child != NOSTREAM)
        {
            WalkTree(e.Child, e.Path, depth + 1);
        }

        WalkTree(e.Right, parentPath, depth);
    }

    /// <summary>Read a stream by its full path (e.g. "/RSeStorage/RSeDbRevisionInfo").</summary>
    public byte[] ReadStream(string path)
    {
        foreach (DirEntry e in Directory)
        {
            if ((e.Type == 2) && string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return ReadEntry(e);
            }
        }

        throw new FileNotFoundException($"Stream not found: {path}");
    }

    public byte[] ReadEntry(DirEntry e)
    {
        if (e.Type != 2)
        {
            throw new InvalidOperationException("Not a stream entry.");
        }

        if (e.Size < MiniStreamCutoff)
        {
            return ReadMiniChain(e.StartSector, e.Size);
        }

        return ReadFatChain(e.StartSector, e.Size);
    }

    // ---- Chain readers ----
    private byte[] ReadFatChain(uint start, long size = -1)
    {
        using MemoryStream mem = new();
        uint s = start; int guard = 0;
        while (s != ENDOFCHAIN && s != FREESECT && guard++ < Fat.Length + 4)
        {
            int off = SectorOffset(s);
            mem.Write(_data, off, Math.Min(SectorSize, _data.Length - off));
            s = NextFat(s);
        }
        return Trim(mem.ToArray(), size);
    }

    private byte[] ReadMiniChain(uint start, long size)
    {
        using MemoryStream mem = new();
        uint s = start; int guard = 0;
        while (s != ENDOFCHAIN && s != FREESECT && guard++ < MiniFat.Length + 4)
        {
            int off = (int)s * MiniSectorSize;
            if (off + MiniSectorSize <= _miniStream.Length)
            {
                mem.Write(_miniStream, off, MiniSectorSize);
            }

            s = (s < MiniFat.Length) ? MiniFat[s] : ENDOFCHAIN;
        }
        return Trim(mem.ToArray(), size);
    }

    private static byte[] Trim(byte[] b, long size)
    {
        if (size < 0 || size >= b.Length)
        {
            return b;
        }

        byte[] r = new byte[size];
        Array.Copy(b, r, size);
        return r;
    }

    private uint NextFat(uint s) => s < Fat.Length ? Fat[s] : ENDOFCHAIN;
    private int  SectorOffset(uint s) => (int)((s + 1) * SectorSize);
    private int  U16(int off) => BitConverter.ToUInt16(_data, off);
    private uint U32(int off) => BitConverter.ToUInt32(_data, off);

    private static DateTime? ToDate(long filetime)
    {
        if (filetime <= 0)
        {
            return null;
        }

        try { return DateTime.FromFileTimeUtc(filetime); } catch { return null; }
    }

    public void Dispose() { }
}