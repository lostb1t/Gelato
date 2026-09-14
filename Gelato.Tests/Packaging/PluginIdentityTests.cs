using System.Text.RegularExpressions;
using Gelato.Tests.Support;

namespace Gelato.Tests.Packaging;

/// <summary>
/// The plugin's identity lives in three files that nothing else keeps in sync:
/// Plugin.cs (compiled constants), build.yaml (what jprm packages and the manifest
/// advertises), and Gelato.csproj (which Jellyfin ABI we actually compiled against).
/// These tests are the only thing that stops them drifting apart.
/// </summary>
public class PluginIdentityTests
{
    private static readonly BuildManifest Manifest = BuildManifest.Load(
        RepoPaths.File("build.yaml")
    );
    private static readonly string PluginSource = File.ReadAllText(RepoPaths.File("Plugin.cs"));
    private static readonly string ProjectFile = File.ReadAllText(RepoPaths.File("Gelato.csproj"));

    [Fact]
    public void PluginCs_Guid_MatchesBuildYaml()
    {
        var match = Regex.Match(PluginSource, @"Guid\.Parse\(""(?<guid>[0-9A-Fa-f-]{36})""\)");
        Assert.True(match.Success, "Plugin.cs no longer contains Guid.Parse(\"...\")");

        Assert.Equal(Guid.Parse(Manifest.Guid), Guid.Parse(match.Groups["guid"].Value));
    }

    [Fact]
    public void PluginCs_Name_MatchesBuildYaml()
    {
        var match = Regex.Match(
            PluginSource,
            @"public override string Name => ""(?<name>[^""]+)"";"
        );
        Assert.True(match.Success, "Plugin.cs no longer declares Name => \"...\"");

        Assert.Equal(Manifest.Name, match.Groups["name"].Value);
    }

    [Fact]
    public void BuildYaml_TargetAbi_MatchesJellyfinControllerPackage()
    {
        var match = Regex.Match(
            ProjectFile,
            @"Include=""Jellyfin\.Controller""\s+Version=""(?<ver>[^""]+)"""
        );
        Assert.True(match.Success, "Gelato.csproj has no Jellyfin.Controller PackageReference");

        // targetAbi is 4-part (12.0.0.0); the package version is 3-part (12.0.0).
        Assert.Equal(
            NormaliseToFourParts(match.Groups["ver"].Value),
            NormaliseToFourParts(Manifest.TargetAbi)
        );
    }

    [Fact]
    public void BuildYaml_Framework_MatchesProjectTargetFramework()
    {
        var match = Regex.Match(ProjectFile, @"<TargetFramework>(?<tfm>[^<]+)</TargetFramework>");
        Assert.True(match.Success, "Gelato.csproj has no <TargetFramework>");

        Assert.Equal(match.Groups["tfm"].Value, Manifest.Framework);
    }

    [Fact]
    public void BuildYaml_Version_IsFourPartNumeric()
    {
        Assert.True(
            Version.TryParse(Manifest.Version, out var v) && v.Revision >= 0,
            $"build.yaml version '{Manifest.Version}' is not major.minor.build.revision"
        );
    }

    [Fact]
    public void BuildYaml_Artifacts_IncludesPluginAssembly()
    {
        Assert.Contains("Gelato.dll", Manifest.Artifacts);
    }

    [Fact]
    public void BuildYaml_Artifacts_AllExistInBuildOutput()
    {
        // The test project's output directory holds Gelato.dll (project reference) and every
        // transitive NuGet assembly, which is the same set jprm packages from Gelato's output.
        var outputDir = Path.GetDirectoryName(typeof(GelatoPlugin).Assembly.Location)!;

        var missing = Manifest
            .Artifacts.Where(a => !File.Exists(Path.Combine(outputDir, a)))
            .ToList();

        var missingList = string.Join(", ", missing);
        Assert.True(
            missing.Count == 0,
            $"build.yaml lists artifacts that the build does not produce: {missingList}"
        );
    }

    private static string NormaliseToFourParts(string version)
    {
        var parts = version.Split('.').ToList();
        while (parts.Count < 4)
            parts.Add("0");
        return string.Join('.', parts.Take(4));
    }
}
