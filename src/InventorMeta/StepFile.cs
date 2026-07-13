using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ExtrabbitCode.Inventor.MetaReader;

internal sealed class StepFileInfo
{
    public string Description = "";
    public string ImplementationLevel = "";
    public string Name = "";
    public string TimeStamp = "";
    public List<string> Authors = [];
    public List<string> Organizations = [];
    public string PreprocessorVersion = "";
    public string OriginatingSystem = "";
    public string Authorisation = "";
    public List<string> Schemas = [];
    public List<string> Products = [];
    public List<string> SolidBodies = [];
    public Dictionary<string, int> EntityCounts = new(StringComparer.OrdinalIgnoreCase);
    public string StructureText = "";
    public int LineCount;
    public long FileSizeBytes;
}

internal static class StepFile
{
    public static bool IsStepExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".stp" or ".step";

    public static bool LooksLikeStepFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            byte[] head = new byte[Math.Min(512, (int)Math.Min(new FileInfo(path).Length, int.MaxValue))];
            using FileStream fs = File.OpenRead(path);
            int read = fs.Read(head, 0, head.Length);
            string text = Encoding.ASCII.GetString(head, 0, read);
            return text.Contains("ISO-10303-21", StringComparison.OrdinalIgnoreCase)
                && text.Contains("HEADER", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static StepFileInfo Read(string path)
    {
        string text = File.ReadAllText(path, Encoding.Latin1);
        StepFileInfo info = new()
        {
            FileSizeBytes = new FileInfo(path).Length,
            LineCount = text.Count(c => c == '\n') + 1
        };

        string noComments = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
        ParseHeader(noComments, info);
        ParseData(noComments, info);
        info.StructureText = BuildStructureText(path, info, noComments);
        return info;
    }

    private static void ParseHeader(string text, StepFileInfo info)
    {
        if (EntityArgs(text, "FILE_DESCRIPTION") is string descArgs)
        {
            List<string> parts = SplitTopLevel(descArgs);
            info.Description = ParseStringList(parts.ElementAtOrDefault(0)).FirstOrDefault() ?? "";
            info.ImplementationLevel = ParseStepString(parts.ElementAtOrDefault(1));
        }

        if (EntityArgs(text, "FILE_NAME") is string nameArgs)
        {
            List<string> parts = SplitTopLevel(nameArgs);
            info.Name = ParseStepString(parts.ElementAtOrDefault(0));
            info.TimeStamp = ParseStepString(parts.ElementAtOrDefault(1));
            info.Authors = ParseStringList(parts.ElementAtOrDefault(2));
            info.Organizations = ParseStringList(parts.ElementAtOrDefault(3));
            info.PreprocessorVersion = ParseStepString(parts.ElementAtOrDefault(4));
            info.OriginatingSystem = ParseStepString(parts.ElementAtOrDefault(5));
            info.Authorisation = ParseStepString(parts.ElementAtOrDefault(6));
        }

        if (EntityArgs(text, "FILE_SCHEMA") is string schemaArgs)
        {
            info.Schemas = ParseStringList(schemaArgs);
        }
    }

    private static void ParseData(string text, StepFileInfo info)
    {
        foreach (Match m in Regex.Matches(text, @"(?m)^\s*#\d+\s*=\s*([A-Z0-9_]+)\s*\("))
        {
            string type = m.Groups[1].Value;
            info.EntityCounts[type] = info.EntityCounts.TryGetValue(type, out int count) ? count + 1 : 1;
        }

        foreach (Match m in Regex.Matches(text, @"PRODUCT\s*\((.*?)\)\s*;", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            List<string> parts = SplitTopLevel(m.Groups[1].Value);
            string name = ParseStepString(parts.ElementAtOrDefault(1));
            AddDistinct(info.Products, name.Length > 0 ? name : ParseStepString(parts.ElementAtOrDefault(0)));
        }

        foreach (Match m in Regex.Matches(text, @"MANIFOLD_SOLID_BREP\s*\(\s*('(?:''|[^'])*')", RegexOptions.IgnoreCase))
        {
            AddDistinct(info.SolidBodies, ParseStepString(m.Groups[1].Value));
        }
    }

    private static void AddDistinct(List<string> list, string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !list.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(value);
        }
    }

    private static string BuildStructureText(string path, StepFileInfo info, string text)
    {
        StringBuilder sb = new();
        sb.AppendLine("STEP ISO-10303-21");
        sb.AppendLine($"File       {Path.GetFileName(path)}");
        sb.AppendLine($"Size       {FormatBytes(info.FileSizeBytes)}");
        if (info.Schemas.Count > 0) { sb.AppendLine($"Schema     {string.Join(", ", info.Schemas)}"); }
        if (info.OriginatingSystem.Length > 0) { sb.AppendLine($"Origin     {info.OriginatingSystem}"); }
        if (info.PreprocessorVersion.Length > 0) { sb.AppendLine($"Processor  {info.PreprocessorVersion}"); }
        if (info.TimeStamp.Length > 0) { sb.AppendLine($"Created    {info.TimeStamp}"); }

        sb.AppendLine();
        sb.AppendLine("DATA entity counts");
        foreach (KeyValuePair<string, int> kv in info.EntityCounts
                     .OrderByDescending(kv => kv.Value)
                     .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                     .Take(40))
        {
            sb.AppendLine($"{kv.Key,-42}{kv.Value,8:N0}");
        }

        if (info.SolidBodies.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Solid bodies");
            foreach (string body in info.SolidBodies.Take(50))
            {
                sb.AppendLine("  " + body);
            }
        }

        sb.AppendLine();
        sb.AppendLine("Source preview");
        foreach (string line in text.Replace("\r\n", "\n").Split('\n').Take(120))
        {
            sb.AppendLine(line);
        }

        return sb.ToString();
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024 * 1024 ? (bytes / 1024.0 / 1024.0).ToString("0.##", CultureInfo.InvariantCulture) + " MB"
      : bytes >= 1024        ? (bytes / 1024.0).ToString("0.##", CultureInfo.InvariantCulture) + " KB"
      :                        bytes.ToString(CultureInfo.InvariantCulture) + " bytes";

    private static string? EntityArgs(string text, string entityName)
    {
        Match m = Regex.Match(text, $@"\b{Regex.Escape(entityName)}\b", RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            return null;
        }

        int open = text.IndexOf('(', m.Index + m.Length);
        if (open < 0)
        {
            return null;
        }

        bool inString = false;
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\'')
            {
                if (inString && i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i++;
                    continue;
                }
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return text[(open + 1)..i];
                }
            }
        }

        return null;
    }

    private static List<string> SplitTopLevel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        List<string> parts = [];
        int start = 0, depth = 0;
        bool inString = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\'')
            {
                if (inString && i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i++;
                    continue;
                }
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (c == '(') { depth++; }
            else if (c == ')') { depth--; }
            else if (c == ',' && depth == 0)
            {
                parts.Add(text[start..i].Trim());
                start = i + 1;
            }
        }
        parts.Add(text[start..].Trim());
        return parts;
    }

    private static List<string> ParseStringList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        List<string> result = [];
        foreach (Match m in Regex.Matches(text, @"'(?:''|[^'])*'"))
        {
            string s = ParseStepString(m.Value);
            if (s.Length > 0)
            {
                result.Add(s);
            }
        }
        return result;
    }

    private static string ParseStepString(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return "";
        }

        token = token.Trim();
        if (token.StartsWith("'") && token.EndsWith("'") && token.Length >= 2)
        {
            token = token[1..^1].Replace("''", "'");
        }
        return DecodeStepEscapes(token);
    }

    private static string DecodeStepEscapes(string text)
    {
        StringBuilder sb = new();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 3 < text.Length && text[i + 1] == 'X')
            {
                if (text[i + 2] == '\\' && i + 4 < text.Length && IsHex(text[i + 3]) && IsHex(text[i + 4]))
                {
                    string hex = text.Substring(i + 3, 2);
                    sb.Append((char)Convert.ToInt32(hex, 16));
                    i += 4;
                    continue;
                }

                if ((text[i + 2] == '2' || text[i + 2] == '4') && i + 3 < text.Length && text[i + 3] == '\\')
                {
                    string terminator = "\\X0\\";
                    int end = text.IndexOf(terminator, i + 4, StringComparison.Ordinal);
                    if (end > i)
                    {
                        string hex = text[(i + 4)..end];
                        for (int p = 0; p + 3 < hex.Length; p += 4)
                        {
                            sb.Append((char)Convert.ToInt32(hex.Substring(p, 4), 16));
                        }
                        i = end + terminator.Length - 1;
                        continue;
                    }
                }
            }

            sb.Append(text[i]);
        }
        return sb.ToString();
    }

    private static bool IsHex(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}
