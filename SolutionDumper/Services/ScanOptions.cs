using System;
using System.Collections.Generic;
using System.Text;

namespace SolutionDumper.Services;

public sealed class ScanOptions
{
    public long MaxFileSizeBytes { get; init; } = 512 * 1024;

    public HashSet<string> IncludeExtensions { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        // C# / MSBuild
        ".cs", ".csproj", ".props", ".targets", ".sln",
        ".resx", ".slnx", ".xaml", ".xaml.cs",
    
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

    public HashSet<string> ExcludedDirectories { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin","obj",".git",".vs","packages","node_modules"
    };
}
