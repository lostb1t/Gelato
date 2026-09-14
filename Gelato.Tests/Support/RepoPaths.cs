namespace Gelato.Tests.Support;

/// <summary>
/// Finds repository files from wherever the test assembly is running. Walks up from the
/// test output directory until it finds the directory holding both build.yaml and
/// Gelato.sln, so it works for Debug, Release, and IDE runners alike.
/// </summary>
public static class RepoPaths
{
    public static string Root { get; } = Locate();

    public static string File(string relative) => Path.Combine(Root, relative);

    private static string Locate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var hasManifest = System.IO.File.Exists(Path.Combine(dir.FullName, "build.yaml"));
            var hasSolution = System.IO.File.Exists(Path.Combine(dir.FullName, "Gelato.sln"));
            if (hasManifest && hasSolution)
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find repo root (build.yaml + Gelato.sln) above {AppContext.BaseDirectory}"
        );
    }
}
