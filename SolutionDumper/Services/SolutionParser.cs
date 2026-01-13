using System.IO;
using System.Text.RegularExpressions;
using System.Xml;

namespace SolutionDumper.Services;

public sealed record SlnProject(string Name, string CsprojPath);

public static class SolutionParser
{
    private static readonly Regex ProjectLine = new(
        @"Project\([^)]+\)\s*=\s*""([^""]+)""\s*,\s*""([^""]+\.csproj)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<SlnProject> Parse(string slnPath)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(slnPath))!;
        var result = new List<SlnProject>(16);

        var ext = Path.GetExtension(slnPath);
        if (ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            ParseSlnx(slnPath, root, result);
        }
        else
        {
            ParseSln(slnPath, root, result);
        }

        return result;
    }

    private static void ParseSln(
        string slnPath,
        string root,
        List<SlnProject> result)
    {
        foreach (var line in File.ReadLines(slnPath))
        {
            var m = ProjectLine.Match(line);
            if (!m.Success)
                continue;

            var rel = m.Groups[2].Value;
            var full = Path.GetFullPath(Path.Combine(root, rel));

            if (File.Exists(full))
                result.Add(new SlnProject(m.Groups[1].Value, full));
        }
    }

    private static void ParseSlnx(
        string slnxPath,
        string root,
        List<SlnProject> result)
    {
        using var reader = XmlReader.Create(
            slnxPath,
            new XmlReaderSettings
            {
                IgnoreComments = true,
                IgnoreWhitespace = true,
                DtdProcessing = DtdProcessing.Ignore
            });

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element ||
                reader.Name != "Project")
                continue;

            var path = reader.GetAttribute("Path");
            if (path == null)
                continue;

            var full = Path.GetFullPath(Path.Combine(root, path));
            if (!File.Exists(full))
                continue;

            var name =
                reader.GetAttribute("Name")
                ?? Path.GetFileNameWithoutExtension(path);

            result.Add(new SlnProject(name, full));
        }
    }
}
