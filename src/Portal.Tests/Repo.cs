using System.Runtime.CompilerServices;

namespace Portal.Tests;

/// <summary>Reads files committed under the repository root, so tests pin the real artifacts
/// rather than a copy in the output directory that can drift.</summary>
public static class Repo
{
    public static string ReadText(string relativePath) => File.ReadAllText(Path(relativePath));

    public static bool Exists(string relativePath) => File.Exists(Path(relativePath));

    /// <summary>Repository-relative paths, so an assertion message names the file a reader can
    /// open rather than an absolute path from whichever machine ran the tests.</summary>
    public static IEnumerable<string> Files(string relativeDirectory, string pattern) =>
        Directory.Exists(Path(relativeDirectory))
            ? Directory.EnumerateFiles(Path(relativeDirectory), pattern, SearchOption.AllDirectories)
                .Select(path => System.IO.Path.GetRelativePath(Root, path).Replace('\\', '/'))
            : [];

    private static string Path(string relativePath) => System.IO.Path.Combine(
        Root, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private static string Root
    {
        get
        {
            // src/Portal.Tests/Repo.cs -> repository root
            var here = System.IO.Path.GetDirectoryName(SourcePath())!;
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(here, "..", ".."));
        }
    }

    private static string SourcePath([CallerFilePath] string path = "") => path;
}
