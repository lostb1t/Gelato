using System.Text.RegularExpressions;

namespace Gelato.Tests.Support;

/// <summary>
/// Reads the handful of fields the tests need from build.yaml. The file is flat and
/// authored by us, so a purpose-built reader is more honest than a YAML dependency:
/// if the shape changes, this fails loudly and the test that relies on it says why.
/// </summary>
public sealed class BuildManifest
{
    private static readonly Regex Scalar = new(
        @"^(?<key>[A-Za-z]+):\s*""?(?<value>[^""]*?)""?\s*$",
        RegexOptions.Compiled
    );
    private static readonly Regex ListItem = new(
        @"^\s+-\s*""?(?<value>[^""]+?)""?\s*$",
        RegexOptions.Compiled
    );

    public required string Name { get; init; }
    public required string Guid { get; init; }
    public required string Version { get; init; }
    public required string TargetAbi { get; init; }
    public required string Framework { get; init; }
    public required string Owner { get; init; }
    public required IReadOnlyList<string> Artifacts { get; init; }

    public static BuildManifest Load(string path)
    {
        var scalars = new Dictionary<string, string>(StringComparer.Ordinal);
        var artifacts = new List<string>();
        var inArtifacts = false;

        foreach (var raw in File.ReadLines(path))
        {
            if (inArtifacts)
            {
                var item = ListItem.Match(raw);
                if (item.Success)
                {
                    artifacts.Add(item.Groups["value"].Value);
                    continue;
                }
                inArtifacts = false;
            }

            var scalar = Scalar.Match(raw);
            if (!scalar.Success)
                continue;

            var key = scalar.Groups["key"].Value;
            if (key == "artifacts")
            {
                inArtifacts = true;
                continue;
            }
            scalars[key] = scalar.Groups["value"].Value;
        }

        string Require(string key) =>
            scalars.TryGetValue(key, out var v) && v.Length > 0
                ? v
                : throw new InvalidDataException($"build.yaml is missing required key '{key}'");

        return new BuildManifest
        {
            Name = Require("name"),
            Guid = Require("guid"),
            Version = Require("version"),
            TargetAbi = Require("targetAbi"),
            Framework = Require("framework"),
            Owner = Require("owner"),
            Artifacts = artifacts,
        };
    }
}
