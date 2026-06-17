using System;
using System.Collections.Generic;

namespace InventorMeta;

/// <summary>
/// Friendly-name tables for Inventor's OLE property sets.
/// The two documented sets (Design Tracking, Summary Information) come straight
/// from the Inventor API enums PropertiesForDesignTrackingPropertiesEnum /
/// PropertiesForSummaryInformationEnum, where the enum integer == the OLE PID.
/// The two internal sets are undocumented; their PIDs are marked "(internal)".
/// </summary>
public static class InventorProps
{
    // ---- FMTID -> human set name + document class GUIDs ----
    public static readonly Dictionary<Guid, string> SetNames = New(new()
    {
        ["F29F85E0-4FF9-1068-AB91-08002B27B3D9"] = "Summary Information",
        ["D5CDD502-2E9C-101B-9397-08002B2CF9AE"] = "Document Summary Information",
        ["D5CDD505-2E9C-101B-9397-08002B2CF9AE"] = "Custom (User Defined) Properties",
        ["32853F0F-3444-11D1-9E93-0060B03C1CA6"] = "Design Tracking Properties",
        ["3D38DE39-0588-4C14-BB37-18F4D5DD31C7"] = "Inventor Summary Information",
        ["8CF58000-DA66-4AE6-8FF0-7B58406FB049"] = "Inventor Document Summary Information",
        ["9929ADB8-6407-413E-B3DC-CB9AD2F564B7"] = "Inventor User Defined Properties",
        ["D861FB30-3136-11D1-9E92-0060B03C1CA6"] = "Design Tracking Control (internal)",
        ["BB586990-AF3E-11D3-95A9-00A0C9B6E37A"] = "Private Model Information (internal)",
        ["02657684-6AD0-49EC-BBD2-9CC4E9293E60"] = "_PostAdaInternalDateMigration (internal)",
        ["C80CD01B-BCEA-4439-A651-1B1A3E3822BB"] = "Assembly Model Info (internal)",
    });

    public static readonly Dictionary<Guid, string> DocClass = New(new()
    {
        ["4D29B490-49B2-11D0-93C3-7E0706000000"] = "Inventor Part (.ipt)",
        ["E60F81E1-49B3-11D0-93C3-7E0706000000"] = "Inventor Assembly (.iam)",
        ["BBF9FDF1-52DC-11D0-8C04-0800090BE8EC"] = "Inventor Drawing (.idw)",
        ["76283A80-50DD-11D3-A7E3-00C04F79D7BC"] = "Inventor Presentation (.ipn)",
    });

    // ---- Design Tracking Properties: {32853F0F-3444-11D1-9E93-0060B03C1CA6} ----
    // Source: PropertiesForDesignTrackingPropertiesEnum (Inventor API). Enum value == PID.
    public static readonly Dictionary<uint, string> DesignTracking = new()
    {
        [4]="Creation Date", [5]="Part Number", [7]="Project", [9]="Cost Center",
        [10]="Checked By", [11]="Date Checked", [12]="Engr Approved By", [13]="Engr Date Approved",
        [17]="User Status", [20]="Material", [21]="Part Property Revision Id", [23]="Catalog Web Link",
        [28]="Part Icon", [29]="Description", [30]="Vendor", [31]="Document SubType",
        [32]="Document SubType Name", [33]="Proxy Refresh Date", [34]="Mfg Approved By",
        [35]="Date Mfg Approved", [36]="Cost", [37]="Standard", [40]="Design Status",
        [41]="Designer", [42]="Engineer", [43]="Authority", [44]="Parameterized Template",
        [45]="Template Row", [46]="External Property Revision Id", [47]="Standard Revision",
        [48]="Manufacturer", [49]="Standards Organization", [50]="Language",
        [51]="Drawing Defer Update", [55]="Stock Number", [56]="Categories", [57]="Weld Material",
        [58]="Mass", [59]="Surface Area", [60]="Volume", [61]="Density", [62]="Valid Mass Props",
        [63]="Flat Pattern Width", [64]="Flat Pattern Length", [65]="Flat Pattern Area",
        [66]="Sheet Metal Rule", [67]="Last Updated With", [71]="Material Identifier",
        [72]="Appearance", [73]="Flat Pattern Defer Update",
    };

    // ---- Summary Information: {F29F85E0-4FF9-1068-AB91-08002B27B3D9} (standard MS) ----
    public static readonly Dictionary<uint, string> SummaryInfo = new()
    {
        [2]="Title", [3]="Subject", [4]="Author", [5]="Keywords", [6]="Comments",
        [8]="Last Saved By", [9]="Revision Number", [12]="Creation Time",
        [13]="Last Saved Time", [17]="Thumbnail", [18]="Application Name",
    };

    // ---- Document Summary Information: {D5CDD502-...} (standard MS) ----
    // Inventor's "Inventor Document Summary Information" {8CF58000-...} uses the same scheme.
    public static readonly Dictionary<uint, string> DocSummaryInfo = new()
    {
        [2]="Category", [3]="Presentation Target", [4]="Byte Count", [5]="Line Count",
        [6]="Paragraph Count", [7]="Slide Count", [8]="Note Count", [9]="Hidden Slides",
        [11]="Scale Crop", [14]="Manager", [15]="Company", [16]="Links Up To Date",
    };

    // ---- Design Tracking Control: {D861FB30-...} (internal, inferred) ----
    public static readonly Dictionary<uint, string> DesignTrackingControl = new()
    {
        [16]="Last Saved By (internal)", [17]="Last Save Time (internal)",
        [22]="Build Number (internal)",
    };

    /// <summary>Resolve a PID to a friendly name for a given section FMTID.</summary>
    public static string Name(Guid fmt, uint pid)
    {
        string f = fmt.ToString().ToUpperInvariant();
        Dictionary<uint, string>? map = f switch
        {
            "32853F0F-3444-11D1-9E93-0060B03C1CA6" => DesignTracking,
            "F29F85E0-4FF9-1068-AB91-08002B27B3D9" => SummaryInfo,
            "3D38DE39-0588-4C14-BB37-18F4D5DD31C7" => SummaryInfo,   // Inventor Summary Information (same scheme)
            "D5CDD502-2E9C-101B-9397-08002B2CF9AE" => DocSummaryInfo,
            "8CF58000-DA66-4AE6-8FF0-7B58406FB049" => DocSummaryInfo, // Inventor Document Summary Information
            "D861FB30-3136-11D1-9E92-0060B03C1CA6" => DesignTrackingControl,
            _ => null
        };
        if (map != null && map.TryGetValue(pid, out string? n))
        {
            return n;
        }

        return $"PID{pid}";
    }

    /// <summary>Design Status enum values (Design Tracking PID 40).</summary>
    public static string DesignStatus(int v) => v switch
    {
        1 => "Work In Progress", 2 => "Pending", 3 => "Released", _ => v.ToString()
    };

    private static Dictionary<Guid, string> New(Dictionary<string, string> src)
    {
        Dictionary<Guid, string> d = new();
        foreach (KeyValuePair<string, string> kv in src)
        {
            d[new Guid(kv.Key)] = kv.Value;
        }

        return d;
    }
}