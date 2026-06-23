using System;
using System.Collections.Generic;
using System.Text;

namespace ExtrabbitCode.Inventor.MetaReader;

/// <summary>
/// Minimal reader for OLE Property Sets ([MS-OLEPS]) - the standard Microsoft
/// format Inventor uses to store iProperties (Summary, Document Summary, and the
/// Inventor-specific "Design Tracking"/"Content"/"User Defined" property sets).
/// A stream is a property set when it begins with the bytes FE FF.
/// </summary>
public static class PropertySet
{
    public sealed class Blob
    {
        public string Kind; public byte[] Data;
        public Blob(string k, byte[] d){Kind=k;Data=d;}
        public override string ToString() => $"<{Kind} {Data.Length} bytes>";
    }

    public sealed class Prop
    {
        public uint   Id;
        public string Name = "";   // resolved via the section dictionary or well-known PID tables
        public ushort Type;
        public object? Value;
        public string TypeName => VtName(Type);
    }

    public sealed class Section
    {
        public Guid FmtId;
        public string FmtNameStr = "";
        public List<Prop> Props = [];
    }

    public sealed class ParsedSet
    {
        public Guid Clsid;
        public List<Section> Sections = [];
    }

    public static bool IsPropertySet(byte[] b) => b is [0xFE, 0xFF, ..];

    public static ParsedSet Parse(byte[] b)
    {
        ParsedSet set = new() {
            // ---- header ----
            // 0: ByteOrder(2)=FFFE  2: Version(2)  4: SystemId(4)  8: CLSID(16)  24: cSections(4)
            Clsid = new Guid(new ReadOnlySpan<byte>(b, 8, 16)) };
        int cSections = (int)U32(b, 24);
        int p = 28;
        List<(Guid fmt, int off)> sectionLocs = [];
        for (int i = 0; i < cSections; i++)
        {
            Guid fmt = new(new ReadOnlySpan<byte>(b, p, 16));
            int off = (int)U32(b, p + 16);
            sectionLocs.Add((fmt, off));
            p += 20;
        }

        foreach ((Guid fmt, int off) in sectionLocs)
        {
            Section sec = new() { FmtId = fmt, FmtNameStr = FmtName(fmt) };
            // section header: cb(4) cProps(4) then cProps * {propid(4) offset(4)}
            int cProps = (int)U32(b, off + 4);
            List<(uint id, int o)> entries = [];
            int q = off + 8;
            for (int i = 0; i < cProps; i++)
            {
                uint id = U32(b, q);
                int o = (int)U32(b, q + 4);
                entries.Add((id, o));
                q += 8;
            }

            // codepage (propid 1) governs ANSI string decoding
            int codePage = 1252;
            foreach ((uint id, int o) in entries)
            {
                if (id == 1) { try { codePage = (short)U16(b, off + o + 4); }
                    catch
                    {
                        // ignored
                    }
                }
            }

            // dictionary (propid 0) maps custom propids -> names
            Dictionary<uint, string> dict = new();
            foreach ((uint id, int o) in entries)
            {
                if (id == 0)
                {
                    ParseDictionary(b, off + o, codePage, dict);
                }
            }

            foreach ((uint id, int o) in entries)
            {
                if (id == 0)
                {
                    continue; // dictionary, already handled
                }

                int vp = off + o;
                ushort type = U16(b, vp);
                object? val;
                try { val = ReadValue(b, vp, codePage); } catch { val = "<unreadable>"; }
                sec.Props.Add(new Prop
                {
                    Id = id,
                    Type = type,
                    Value = val,
                    Name = dict.TryGetValue(id, out string? nm) ? nm : WellKnownPid(fmt, id)
                });
            }
            set.Sections.Add(sec);
        }
        return set;
    }

    private static void ParseDictionary(byte[] b, int off, int codePage, Dictionary<uint, string> dict)
    {
        if (off + 4 > b.Length)
        {
            return;
        }

        uint cEntries = U32(b, off);
        int p = off + 4;
        bool unicode = codePage == 1200;
        for (uint i = 0; i < cEntries && p + 8 <= b.Length; i++)
        {
            uint pid = U32(b, p);
            int len = (int)U32(b, p + 4);   // count of CHARACTERS incl. terminating NUL
            p += 8;
            string name;
            if (unicode)
            {
                int byteLen = len * 2;
                if (p + byteLen > b.Length)
                {
                    break;
                }

                name = Encoding.Unicode.GetString(b, p, byteLen).TrimEnd('\0');
                p += byteLen;
                if ((p - (off)) % 4 != 0)
                {
                    p = ((p + 3) / 4) * 4; // Unicode dict entries pad to 4 bytes
                }
            }
            else
            {
                if (p + len > b.Length)
                {
                    break;
                }

                name = Decode(b, p, len, codePage).TrimEnd('\0');
                p += len; // SBCS: no padding
            }
            dict[pid] = name;
        }
    }

    private static object? ReadValue(byte[] b, int vp, int codePage)
    {
        ushort type = U16(b, vp);
        int d = vp + 4; // data starts after type(2)+pad(2)
        switch (type)
        {
            case 2:  return (short)U16(b, d);                 // VT_I2
            case 3:  return (int)U32(b, d);                   // VT_I4
            case 4:  return BitConverter.ToSingle(b, d);      // VT_R4
            case 5:  return BitConverter.ToDouble(b, d);      // VT_R8
            case 6:  return BitConverter.ToInt64(b, d) / 10000.0; // VT_CY
            case 7:  return OaDate(BitConverter.ToDouble(b, d)); // VT_DATE
            case 11: return U32(b, d) != 0;                   // VT_BOOL
            case 16: return (sbyte)b[d];                      // VT_I1
            case 17: return b[d];                             // VT_UI1
            case 18: return U16(b, d);                        // VT_UI2
            case 19: return U32(b, d);                        // VT_UI4
            case 20: return BitConverter.ToInt64(b, d);       // VT_I8
            case 21: return BitConverter.ToUInt64(b, d);      // VT_UI8
            case 8:  { int n=(int)U32(b,d); return Decode(b,d+4,n,codePage).TrimEnd('\0'); } // VT_BSTR
            case 30: { int n=(int)U32(b,d); return Decode(b,d+4,n,codePage).TrimEnd('\0'); } // VT_LPSTR
            case 31: { int n=(int)U32(b,d); return Encoding.Unicode.GetString(b,d+4,Math.Max(0,(n-1)*2)); } // VT_LPWSTR (n=chars incl NUL)
            case 64: return FileTime(BitConverter.ToInt64(b, d)); // VT_FILETIME
            case 65: { int n=(int)U32(b,d); byte[] r=new byte[n]; Array.Copy(b,d+4,r,0,Math.Min(n,b.Length-d-4)); return new Blob("BLOB",r); } // VT_BLOB
            case 71: { int n=(int)U32(b,d); byte[] r=new byte[n]; Array.Copy(b,d+4,r,0,Math.Min(n,b.Length-d-4)); return new Blob("CF",r); }   // VT_CF
            case 72: return new Guid(new ReadOnlySpan<byte>(b, d, 16)); // VT_CLSID
            case 0:  return null;  // VT_EMPTY
            case 1:  return null;  // VT_NULL
            default:
                if ((type & 0x1000) != 0)
                {
                    return ReadVector(b, vp, (ushort)(type & 0x0FFF), codePage);
                }

                // Inventor stores a model-state-OVERRIDDEN property as a VT_ARRAY-flagged
                // (0x2000) variant bundling the per-state value first, then the base value.
                if ((type & 0x2000) != 0)
                {
                    return ReadOverride(b, vp, codePage);
                }

                return $"<vt 0x{type:X4}>";
        }
    }

    // Inventor per-model-state override: {type 0x200C}{16-byte header}{inner variant}…
    // The first inner variant is the value for THIS state; later ones are base values.
    private static object ReadOverride(byte[] b, int vp, int codePage)
    {
        // structural: header is 4 dwords, first inner variant at vp+20
        object? v = ReadInner(b, vp + 20, codePage);
        if (v != null)
        {
            return v;
        }

        // fallback: scan for the first decodable inner variant
        for (int p = vp + 4; p + 8 <= b.Length && p < vp + 96; p += 4)
        {
            v = ReadInner(b, p, codePage);
            if (v != null)
            {
                return v;
            }
        }
        return "<state override>";
    }

    private static object? ReadInner(byte[] b, int p, int codePage)
    {
        if (p + 8 > b.Length)
        {
            return null;
        }

        uint vt = U32(b, p);
        switch (vt)
        {
            case 8:   // VT_BSTR (byte length incl. NUL, UTF-16)
            case 31:  // VT_LPWSTR
                {
                    int n = (int)U32(b, p + 4);
                    if (n <= 0 || n > 4096 || p + 8 + n > b.Length)
                    {
                        return null;
                    }

                    string s = Encoding.Unicode.GetString(b, p + 8, n).TrimEnd('\0');
                    return s.Length > 0 ? s : null;
                }
            case 30:  // VT_LPSTR
                {
                    int n = (int)U32(b, p + 4);
                    if (n <= 0 || n > 4096 || p + 8 + n > b.Length)
                    {
                        return null;
                    }

                    string s = Decode(b, p + 8, n, codePage).TrimEnd('\0');
                    return s.Length > 0 ? s : null;
                }
            case 3:  return (int)U32(b, p + 4);                  // VT_I4
            case 2:  return (short)U16(b, p + 4);                // VT_I2
            case 5:  return p + 12 <= b.Length ? BitConverter.ToDouble(b, p + 4) : null; // VT_R8
            case 11: return U32(b, p + 4) != 0;                  // VT_BOOL
            default: return null;
        }
    }

    private static object ReadVector(byte[] b, int vp, ushort baseType, int codePage)
    {
        int d = vp + 4;
        int count = (int)U32(b, d);
        int p = d + 4;
        List<object?> items = [];
        for (int i = 0; i < count && p < b.Length; i++)
        {
            switch (baseType)
            {
                case 31: { int n=(int)U32(b,p); items.Add(Encoding.Unicode.GetString(b,p+4,(n-1)*2)); p += 4 + n*2; if((p&3)!=0)
                    {
                        p=(p+3)&~3;
                    }

                    break; }
                case 30: { int n=(int)U32(b,p); items.Add(Decode(b,p+4,n,codePage).TrimEnd('\0')); p += 4 + n; if((p&3)!=0)
                    {
                        p=(p+3)&~3;
                    }

                    break; }
                case 3:  { items.Add((int)U32(b,p)); p += 4; break; }
                case 5:  { items.Add(BitConverter.ToDouble(b,p)); p += 8; break; }
                default: i = count; break;
            }
        }
        return items;
    }

    // ---------- helpers ----------
    private static ushort U16(byte[] b, int o) => BitConverter.ToUInt16(b, o);
    private static uint   U32(byte[] b, int o) => BitConverter.ToUInt32(b, o);
    private static string Decode(byte[] b, int o, int len, int cp)
    {
        if (len <= 0 || o + len > b.Length)
        {
            return "";
        }

        try { return Encoding.GetEncoding(cp).GetString(b, o, len); }
        catch { return Encoding.Latin1.GetString(b, o, len); }
    }
    private static DateTime? FileTime(long ft) { try { return ft > 0 ? DateTime.FromFileTimeUtc(ft) : null; } catch { return null; } }
    private static DateTime? OaDate(double d) { try { return DateTime.FromOADate(d); } catch { return null; } }

    public static string VtName(ushort t)
    {
        ushort bt = (ushort)(t & 0x0FFF);
        string n = bt switch {
            0=>"EMPTY",1=>"NULL",2=>"I2",3=>"I4",4=>"R4",5=>"R8",6=>"CY",7=>"DATE",8=>"BSTR",
            11=>"BOOL",16=>"I1",17=>"UI1",18=>"UI2",19=>"UI4",20=>"I8",21=>"UI8",
            30=>"LPSTR",31=>"LPWSTR",64=>"FILETIME",65=>"BLOB",71=>"CF",72=>"CLSID",_=>$"0x{bt:X}" };
        return (t & 0x1000) != 0 ? $"VECTOR<{n}>" : n;
    }

    // Known Inventor / OLE property-set FMTIDs
    public static string FmtName(Guid g) => g.ToString().ToUpperInvariant() switch
    {
        "F29F85E0-4FF9-1068-AB91-08002B27B3D9" => "SummaryInformation",
        "D5CDD502-2E9C-101B-9397-08002B2CF9AE" => "DocumentSummaryInformation",
        "D5CDD505-2E9C-101B-9397-08002B2CF9AE" => "UserDefinedProperties",
        "32853F0F-3444-11D1-9E93-0060B03C1CA6" => "Inventor Design Tracking Properties",
        "B9600981-DEBA-4F0C-8B17-C0917EE5C0E0" => "Inventor Private",
        _ => ""
    };

    // Well-known PIDs for the two standard sets (used when no dictionary entry exists)
    private static string WellKnownPid(Guid fmt, uint id)
    {
        string f = fmt.ToString().ToUpperInvariant();
        if (f == "F29F85E0-4FF9-1068-AB91-08002B27B3D9")
        {
            return id switch { 2=>"Title",3=>"Subject",4=>"Author",5=>"Keywords",6=>"Comments",
                7=>"Template",8=>"LastSavedBy",9=>"RevisionNumber",12=>"CreateTime",13=>"LastSavedTime",
                14=>"NumPages",18=>"AppName",_=>$"PID{id}" };
        }

        if (f == "D5CDD502-2E9C-101B-9397-08002B2CF9AE")
        {
            return id switch { 2=>"Category",3=>"PresentationTarget",6=>"Manager",7=>"Company",
                14=>"Manager",15=>"Company",_=>$"PID{id}" };
        }

        return $"PID{id}";
    }
}