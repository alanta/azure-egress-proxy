using System.Runtime.CompilerServices;

namespace ControlPlane.Tests;

/// <summary>Reads files committed at the repository root, so tests pin the real artifacts teams
/// edit rather than a copy that can drift.</summary>
public static class Repo
{
    public static string ReadText(string relativePath) =>
        File.ReadAllText(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string Root
    {
        get
        {
            // src/ControlPlane.Tests/Repo.cs -> repository root
            var here = Path.GetDirectoryName(SourcePath())!;
            return Path.GetFullPath(Path.Combine(here, "..", ".."));
        }
    }

    private static string SourcePath([CallerFilePath] string path = "") => path;
}
