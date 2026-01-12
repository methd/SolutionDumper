using System.IO;

namespace SolutionDumper.Services;

public static class ProjectScanner
{
    public sealed record ProjectFile(string FullPath, long SizeBytes);

    public static IEnumerable<ProjectFile> EnumerateProjectFiles(string csprojPath, ScanOptions opt)
    {
        var dir = Path.GetDirectoryName(csprojPath)!;

        foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
        {
            if (IsInExcludedDir(file, opt))
                continue;

            var fileLower = file.ToLowerInvariant();
            var ext = Path.GetExtension(file);

            bool allowed =
                opt.IncludeExtensions.Contains(ext) ||
                opt.IncludeExtensions.Any(sfx => fileLower.EndsWith(sfx, StringComparison.OrdinalIgnoreCase));

            if (!allowed)
                continue;

            long size;
            try
            {
                size = new FileInfo(file).Length;
            }
            catch
            {
                continue;
            }

            yield return new ProjectFile(file, size);
        }
    }

    private static bool IsInExcludedDir(string file, ScanOptions opt)
    {
        foreach (var dir in opt.ExcludedDirectories)
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}{dir}{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
