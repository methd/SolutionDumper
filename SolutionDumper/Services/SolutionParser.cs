using System.IO;
using System.Text.RegularExpressions;

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
        var lines = File.ReadAllLines(slnPath);

        var result = new List<SlnProject>();
        foreach (var line in lines)
        {
            var m = ProjectLine.Match(line);
            if (!m.Success) continue;

            var name = m.Groups[1].Value;
            var rel = m.Groups[2].Value.Replace('\\', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(root, rel));

            if (File.Exists(full))
                result.Add(new SlnProject(name, full));
        }
        return result;
    }
}
