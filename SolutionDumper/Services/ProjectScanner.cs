using System.IO;

namespace SolutionDumper.Services;

public sealed class ScanOptions
{
    public HashSet<string> IncludeExtensions { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        // C# / MSBuild
        ".cs", ".csproj", ".props", ".targets", ".sln",
        ".resx",
    
        // Blazor / Razor
        ".razor", ".cshtml",
        ".razor.css",
        ".razor.js",
    
        // Web assets (wwwroot)
        ".js", ".mjs", ".ts",
        ".css", ".scss",
        ".json",
        ".html",
    
        // configs
        ".xml", ".config",
        ".yml", ".yaml",
    
        ".png", ".svg", ".ico", ".jpg", ".jpeg", ".webp",
        ".woff", ".woff2"
    };

    public HashSet<string> ExcludeDirs { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin","obj",".git",".vs","packages","node_modules"
    };

    public long MaxFileSizeBytes { get; set; } = 2 * 1024 * 1024;
}

public static class ProjectScanner
{
    public static IEnumerable<string> EnumerateProjectFiles(string csprojPath, ScanOptions opt)
    {
        var dir = Path.GetDirectoryName(csprojPath)!;
        foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
        {
            if (IsInExcludedDir(file, opt)) continue;

            var fileLower = file.ToLowerInvariant();
            var ext = Path.GetExtension(file);

            bool allowed =
                opt.IncludeExtensions.Contains(ext) ||
                opt.IncludeExtensions.Any(sfx => fileLower.EndsWith(sfx, StringComparison.OrdinalIgnoreCase));

            if (!allowed) continue;

            var info = new FileInfo(file);
            if (info.Length > opt.MaxFileSizeBytes) continue;

            yield return file;
        }
    }

    private static bool IsInExcludedDir(string file, ScanOptions opt)
    {
        var parts = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => opt.ExcludeDirs.Contains(p));
    }
}
