# Test Suite and Release Gate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an xUnit test project and a package-verification script, then reorder the release workflow so nothing is published until both pass.

**Architecture:** `Gelato.Tests/` is a plain xUnit project referencing `Gelato.csproj`; it holds consistency assertions (GUID/name/ABI/artifacts agree across `Plugin.cs`, `build.yaml`, `Gelato.csproj`) and unit tests for the pure logic in `Common.cs`. `scripts/verify-package.sh` inspects the jprm-built zip and is the only thing that needs a packaged artifact. `release.yml` is reordered to test → build → verify → publish, and the Makefile is split into composable targets so publishing can be the last step.

**Tech Stack:** .NET 10 SDK, xUnit 2.9.3, NSubstitute 6.2.0, jprm 1.1.x (Jellyfin Plugin Repository Manager), git-cliff 2.x, GitHub Actions, bash.

**Spec:** `docs/superpowers/specs/2026-09-14-test-suite-and-release-gate-design.md`

## Global Constraints

- Target framework is `net10.0`; `global.json` pins SDK `10.0.0` with `rollForward: latestFeature`.
- Every commit message must be a Conventional Commit (`feat:`, `fix:`, `test:`, `ci:`, `chore:`, `build:`) — CI rejects others.
- Every commit message ends with the line `Claude-Session: https://claude.ai/code/session_01BpSFNaJzUKvd4QEVgTyyt4`.
- All C# must pass `dotnet format --verify-no-changes` (the `lint` CI job) and follow `.editorconfig`: 4-space indent, LF, max line length 100. The repo formats with csharpier (`dotnet tool restore && dotnet csharpier format .`).
- Plugin identity values are: name `Chocolate Gelato`, GUID `E2513B6C-E574-47A5-B89D-CE05BF975685`, `targetAbi` `12.0.0.0`, framework `net10.0`, owner `adamlippert`. Tests assert agreement between files; they never hardcode these values except where the spec calls for pinning (`ToGuid` expectations).
- Never run `make publish`, `make release`, or `make prerelease` locally during this plan. They push commits and create GitHub Releases. The only release cut is the CI verification run in Task 10.
- `dotnet test` builds Debug by default. Nothing may assume a `bin/Release` path.
- Work on `main` directly is not allowed; each task is committed on the branch `feat/test-suite-and-release-gate` and merged fast-forward at the end (Task 10).

## Findings from pre-implementation probing

These were established by running the real code before writing this plan. They change what the tests must assert.

1. **`Utils.ParseToTicks("149")` returns 149 days, not 149 minutes.** `TimeSpan.TryParse` treats a bare integer as days and succeeds, so the "plain number means minutes" branch at the bottom of the method is unreachable. The spec assumed minutes; that assumption was wrong. Tests pin the *actual* behaviour and name it as a known defect. Fixing it is a behaviour change and is out of scope for this plan.
2. **`Utils.ParseToTicks("PT2H29M")` returns 2 hours, dropping the minutes.** The input is lower-cased before `XmlConvert.ToTimeSpan`, which is case-sensitive and throws on `pt2h29m`; the regex fallback then matches `2h` but not `29m` (it requires `min`). Same treatment: pinned, named as a known defect, not fixed here.
3. **jprm rewrites `<Version>` in `Gelato.csproj` when it builds.** After any jprm build the working tree is dirty. The release workflow must `git checkout -- Gelato.csproj` before committing, and anyone building locally must do the same.
4. **`Episode.Series` needs `BaseItem.LibraryManager`.** In Jellyfin 12 the getter is `LibraryManager.GetItemById(SeriesId) as Series`. With `SeriesId` empty and `ParentId` empty it returns `null` without touching the static, so the fallback path is testable bare; the happy path needs the static set to an `ILibraryManager` substitute.
5. Authoritative expected values, computed by running the production logic:

   | Input | `ParseToTicks` | Meaning |
   |---|---|---|
   | `"2:29:00"` | `89400000000` | 2h29m |
   | `"02:29:00"` | `89400000000` | 2h29m |
   | `"2h29min"` | `89400000000` | 2h29m |
   | `"2h 29min"` | `89400000000` | 2h29m |
   | `"1h"` | `36000000000` | 1h |
   | `"90s"` | `900000000` | 1m30s |
   | `"45sec"` | `450000000` | 45s |
   | `"PT90S"` | `900000000` | 1m30s |
   | `"1.02:03:04"` | `937840000000` | 1d 2h3m4s |
   | `"PT2H29M"` | `72000000000` | **2h — defect, minutes lost** |
   | `"149"` | `128736000000000` | **149 days — defect** |
   | `"abc"` | `0` | zero, not null |
   | `""`, `"   "`, `null` | `null` | |

   | `StremioUri.ToString()` | `ToGuid()` |
   |---|---|
   | `stremio://movie/tt0111161` | `a9285927-29f0-9e66-b169-d50acfa84566` |
   | `stremio://series/tt0903747` | `4c9c1c88-1f4c-a1f7-e20b-da196a397d28` |
   | `stremio://series/tt0903747:1:2` | `650580bf-e5d3-4d21-47c9-354edef0f67d` |
   | `stremio://movie/tt0111161/somestream` | `c4165978-24c6-fadf-e431-2668988a9c5f` |

## File structure

**Created**

| Path | Responsibility |
|---|---|
| `Gelato.Tests/Gelato.Tests.csproj` | xUnit project; references `../Gelato.csproj` |
| `Gelato.Tests/Support/RepoPaths.cs` | Locates the repo root and well-known files from the test's output directory |
| `Gelato.Tests/Support/BuildManifest.cs` | Minimal reader for `build.yaml` (scalars + `artifacts` list) |
| `Gelato.Tests/Packaging/PluginIdentityTests.cs` | Consistency assertions across `Plugin.cs`, `build.yaml`, `Gelato.csproj`, build output |
| `Gelato.Tests/Config/PluginConfigurationSerializationTests.cs` | XML round-trip of `PluginConfiguration` |
| `Gelato.Tests/Common/StremioUriTests.cs` | Constructor, `ToString`, pinned `ToGuid`, `FromBaseItem` |
| `Gelato.Tests/Common/UtilsParseToTicksTests.cs` | Characterisation of `ParseToTicks` |
| `Gelato.Tests/Common/EnumMappingTests.cs` | `ToStremio` / `ToBaseItem` |
| `Gelato.Tests/Common/StringExtensionsTests.cs` | `IsUrl` |
| `Gelato.Tests/Common/KeyLockTests.cs` | Single-flight and queued semantics |
| `scripts/verify-package.sh` | Asserts a jprm zip matches `build.yaml` and the intended version |

**Modified**

| Path | Change |
|---|---|
| `Gelato.sln` | Add the test project |
| `Makefile` | Split into `version` / `notes` / `publish` / `release` / `prerelease` / `release-preview` / `test` |
| `.github/workflows/pr-build.yml` | Add `test` job |
| `.github/workflows/main.yml` | Add `test` job |
| `.github/workflows/release.yml` | Reorder: version → test → build → verify → publish → upload → manifest |
| `docs/superpowers/specs/2026-09-14-test-suite-and-release-gate-design.md` | Already corrected for Finding 1 and 2 |

---

### Task 1: Scaffold the test project

**Files:**
- Create: `Gelato.Tests/Gelato.Tests.csproj`
- Create: `Gelato.Tests/Packaging/AssemblySmokeTests.cs`
- Modify: `Gelato.sln`

**Interfaces:**
- Produces: a compiling test project in the solution that every later task adds files to. Namespace root is `Gelato.Tests`.

- [ ] **Step 1: Create the branch**

```bash
cd "/Users/survivalizer/My Projects/gelato"
git checkout main && git pull --ff-only
git checkout -b feat/test-suite-and-release-gate
```

- [ ] **Step 2: Write the project file**

Create `Gelato.Tests/Gelato.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <RootNamespace>Gelato.Tests</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="NSubstitute" Version="6.2.0" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="../Gelato.csproj" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Write the failing smoke test**

Create `Gelato.Tests/Packaging/AssemblySmokeTests.cs`:

```csharp
namespace Gelato.Tests.Packaging;

public class AssemblySmokeTests
{
    [Fact]
    public void GelatoAssembly_LoadsAndIsNamedGelato()
    {
        var asm = typeof(GelatoPlugin).Assembly;

        Assert.Equal("Gelato", asm.GetName().Name);
        Assert.True(File.Exists(asm.Location), $"assembly not on disk: {asm.Location}");
    }
}
```

- [ ] **Step 4: Add the project to the solution and run the test**

```bash
dotnet sln Gelato.sln add Gelato.Tests/Gelato.Tests.csproj
dotnet test Gelato.sln
```

Expected: restore + build succeed, `Passed! - Failed: 0, Passed: 1`. If the build fails with a missing `Jellyfin.*` assembly at test time, the `ProjectReference` is not flowing transitive packages — check that `Gelato.csproj` has no `PrivateAssets="all"` on the Jellyfin packages (it does not as of this plan).

- [ ] **Step 5: Confirm lint passes on the new project**

```bash
dotnet format Gelato.sln --verify-no-changes
```

Expected: exit 0. If it reports whitespace issues, run `dotnet format Gelato.sln` and re-verify.

- [ ] **Step 6: Commit**

```bash
git add Gelato.sln Gelato.Tests/
git commit -F - <<'EOF'
test: scaffold Gelato.Tests xUnit project

Adds an empty-but-compiling test project referencing Gelato.csproj, with
a single smoke test proving the plugin assembly loads. Every later test
file lands in this project.

Claude-Session: https://claude.ai/code/session_01BpSFNaJzUKvd4QEVgTyyt4
EOF
```

---

### Task 2: Consistency assertions across identity files

**Files:**
- Create: `Gelato.Tests/Support/RepoPaths.cs`
- Create: `Gelato.Tests/Support/BuildManifest.cs`
- Create: `Gelato.Tests/Packaging/PluginIdentityTests.cs`

**Interfaces:**
- Produces: `static class RepoPaths { static string Root; static string File(string relative); }` and `sealed class BuildManifest { string Name; string Guid; string Version; string TargetAbi; string Framework; string Owner; IReadOnlyList<string> Artifacts; static BuildManifest Load(string path); }`. Task 7's script mirrors the same parsing rules in bash.

- [ ] **Step 1: Write the repo-root locator**

Create `Gelato.Tests/Support/RepoPaths.cs`:

```csharp
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
```

- [ ] **Step 2: Write the build.yaml reader**

Create `Gelato.Tests/Support/BuildManifest.cs`:

```csharp
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
```

- [ ] **Step 3: Write the failing consistency tests**

Create `Gelato.Tests/Packaging/PluginIdentityTests.cs`:

```csharp
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
    private static readonly string ProjectFile = File.ReadAllText(
        RepoPaths.File("Gelato.csproj")
    );

    [Fact]
    public void PluginCs_Guid_MatchesBuildYaml()
    {
        var match = Regex.Match(PluginSource, @"Guid\.Parse\(""(?<guid>[0-9A-Fa-f-]{36})""\)");
        Assert.True(match.Success, "Plugin.cs no longer contains Guid.Parse(\"...\")");

        Assert.Equal(
            Guid.Parse(Manifest.Guid),
            Guid.Parse(match.Groups["guid"].Value)
        );
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

        Assert.True(
            missing.Count == 0,
            $"build.yaml lists artifacts that the build does not produce: {string.Join(", ", missing)}"
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
```

- [ ] **Step 4: Run and confirm they pass against the current tree**

```bash
dotnet test Gelato.sln --filter "FullyQualifiedName~PluginIdentityTests"
```

Expected: 7 passed. These are green on day one because the values currently agree — that is the point. To see one fail, temporarily change one hex digit of the GUID in `build.yaml`, rerun, observe `PluginCs_Guid_MatchesBuildYaml` fail with both GUIDs printed, then `git checkout -- build.yaml`.

- [ ] **Step 5: Format and commit**

```bash
dotnet tool restore
dotnet csharpier format Gelato.Tests/
dotnet format Gelato.sln --verify-no-changes
git add Gelato.Tests/
git commit -F - <<'EOF'
test: assert plugin identity agrees across Plugin.cs, build.yaml, csproj

The GUID, name, target ABI and artifact list live in three files with
nothing keeping them in sync. These tests fail the build the moment any
of them drift, which is the failure mode that silently produces a
plugin Jellyfin treats as a different plugin or cannot load.

Claude-Session: https://claude.ai/code/session_01BpSFNaJzUKvd4QEVgTyyt4
EOF
```

---

### Task 3: PluginConfiguration XML round-trip

**Files:**
- Create: `Gelato.Tests/Config/PluginConfigurationSerializationTests.cs`

**Interfaces:**
- Consumes: `Gelato.Config.PluginConfiguration`, `CatalogConfig`, `UserConfig` (existing).

- [ ] **Step 1: Write the failing tests**

Jellyfin persists plugin configuration with `System.Xml.Serialization.XmlSerializer` (its `IXmlSerializer` implementation wraps it). If `PluginConfiguration` stops round-tripping, the plugin loads but its settings page breaks and saved settings are lost.

Create `Gelato.Tests/Config/PluginConfigurationSerializationTests.cs`:

```csharp
using System.Xml.Serialization;
using Gelato.Config;

namespace Gelato.Tests.Config;

public class PluginConfigurationSerializationTests
{
    [Fact]
    public void Defaults_Serialize_WithoutThrowing()
    {
        var xml = Serialize(new PluginConfiguration());

        Assert.Contains("<PluginConfiguration", xml);
        Assert.DoesNotContain("Stremio", xml); // [XmlIgnore] fields must stay out
    }

    [Fact]
    public void NonDefaultValues_RoundTrip_Unchanged()
    {
        var userId = Guid.NewGuid();
        var original = new PluginConfiguration
        {
            Url = "https://aio.example.com/abc/manifest.json",
            MoviePath = "/data/gelato/movies",
            SeriesPath = "/data/gelato/series",
            StreamTTL = 120,
            CatalogMaxItems = 7,
            EnableMixed = true,
            FilterUnreleased = true,
            FilterUnreleasedBufferDays = 3,
            DisableSourceCount = false,
            P2PEnabled = true,
            P2PDLSpeed = 1024,
            CreateCollections = true,
            LastSeenServerVersion = "12.0.0",
            Catalogs =
            [
                new CatalogConfig
                {
                    Id = "top",
                    Type = "series",
                    Name = "Top",
                    Enabled = true,
                    MaxItems = 5,
                    CreateCollection = true,
                },
            ],
            UserConfigs =
            [
                new UserConfig
                {
                    UserId = userId,
                    Url = "https://aio.example.com/kid/manifest.json",
                    MoviePath = "/data/kid/movies",
                    SeriesPath = "/data/kid/series",
                    DisableSearch = true,
                },
            ],
        };

        var restored = Deserialize(Serialize(original));

        Assert.Equal(original.Url, restored.Url);
        Assert.Equal(original.MoviePath, restored.MoviePath);
        Assert.Equal(original.SeriesPath, restored.SeriesPath);
        Assert.Equal(original.StreamTTL, restored.StreamTTL);
        Assert.Equal(original.CatalogMaxItems, restored.CatalogMaxItems);
        Assert.Equal(original.EnableMixed, restored.EnableMixed);
        Assert.Equal(original.FilterUnreleased, restored.FilterUnreleased);
        Assert.Equal(original.FilterUnreleasedBufferDays, restored.FilterUnreleasedBufferDays);
        Assert.Equal(original.DisableSourceCount, restored.DisableSourceCount);
        Assert.Equal(original.P2PEnabled, restored.P2PEnabled);
        Assert.Equal(original.P2PDLSpeed, restored.P2PDLSpeed);
        Assert.Equal(original.CreateCollections, restored.CreateCollections);
        Assert.Equal(original.LastSeenServerVersion, restored.LastSeenServerVersion);

        var catalog = Assert.Single(restored.Catalogs);
        Assert.Equal("top", catalog.Id);
        Assert.Equal("series", catalog.Type);
        Assert.True(catalog.Enabled);
        Assert.Equal(5, catalog.MaxItems);
        Assert.True(catalog.CreateCollection);

        var user = Assert.Single(restored.UserConfigs);
        Assert.Equal(userId, user.UserId);
        Assert.Equal("/data/kid/movies", user.MoviePath);
        Assert.True(user.DisableSearch);
    }

    [Fact]
    public void GetBaseUrl_StripsManifestSuffixAndTrailingSlash()
    {
        var cfg = new PluginConfiguration { Url = "https://aio.example.com/abc/manifest.json/" };

        Assert.Equal("https://aio.example.com/abc", cfg.GetBaseUrl());
    }

    [Fact]
    public void GetBaseUrl_Throws_WhenUnconfigured()
    {
        var cfg = new PluginConfiguration { Url = "   " };

        Assert.Throws<InvalidOperationException>(() => cfg.GetBaseUrl());
    }

    private static string Serialize(PluginConfiguration cfg)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, cfg);
        return writer.ToString();
    }

    private static PluginConfiguration Deserialize(string xml)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var reader = new StringReader(xml);
        return (PluginConfiguration)serializer.Deserialize(reader)!;
    }
}
```

- [ ] **Step 2: Run**

```bash
dotnet test Gelato.sln --filter "FullyQualifiedName~PluginConfigurationSerializationTests"
```

Expected: 4 passed. If `Defaults_Serialize_WithoutThrowing` throws `InvalidOperationException: ... cannot be serialized`, a new public member on `PluginConfiguration` has a type XmlSerializer can't handle — that is precisely the regression this test exists to catch; report it rather than weakening the test.

- [ ] **Step 3: Format and commit**

```bash
dotnet csharpier format Gelato.Tests/
dotnet format Gelato.sln --verify-no-changes
git add Gelato.Tests/Config/
git commit -F - <<'EOF'
test: round-trip PluginConfiguration through XmlSerializer

Jellyfin stores plugin settings via XmlSerializer. A configuration
class that stops serialising loads fine and then loses every saved
setting, which no build step would notice.

Claude-Session: https://claude.ai/code/session_01BpSFNaJzUKvd4QEVgTyyt4
EOF
```

---

### Task 4: StremioUri — pinned GUIDs and FromBaseItem

**Files:**
- Create: `Gelato.Tests/Common/StremioUriTests.cs`
- Create: `Gelato.Tests/Common/StremioUriFromEpisodeTests.cs`

**Interfaces:**
- Consumes: `Gelato.StremioUri`, `Gelato.StremioMediaType` (existing, namespace `Gelato`).
- Consumes: `MediaBrowser.Controller.Entities.BaseItem.LibraryManager` static, `MediaBrowser.Controller.Library.ILibraryManager.GetItemById(Guid)`.

- [ ] **Step 1: Write the pure tests**

Create `Gelato.Tests/Common/StremioUriTests.cs`:

```csharp
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Gelato.Tests.Common;

public class StremioUriTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsBlankExternalId(string? externalId)
    {
        Assert.Throws<ArgumentException>(() => new StremioUri(StremioMediaType.Movie, externalId));
    }

    [Fact]
    public void ToString_Movie_WithoutStream()
    {
        var uri = new StremioUri(StremioMediaType.Movie, "tt0111161");

        Assert.Equal("stremio://movie/tt0111161", uri.ToString());
    }

    [Fact]
    public void ToString_Series_WithStream()
    {
        var uri = new StremioUri(StremioMediaType.Series, "tt0903747:1:2", "abc");

        Assert.Equal("stremio://series/tt0903747:1:2/abc", uri.ToString());
    }

    [Fact]
    public void ToString_BlankStreamId_IsTreatedAsNone()
    {
        var uri = new StremioUri(StremioMediaType.Movie, "tt0111161", "   ");

        Assert.Equal("stremio://movie/tt0111161", uri.ToString());
    }

    /// <summary>
    /// ToGuid() is the Jellyfin item ID for every Gelato item in a user's library. These
    /// expectations were computed from the production implementation. If this test fails,
    /// existing libraries will orphan on upgrade — change the derivation only deliberately,
    /// with a migration, and update these values in the same commit.
    /// </summary>
    [Theory]
    [InlineData(StremioMediaType.Movie, "tt0111161", null, "a9285927-29f0-9e66-b169-d50acfa84566")]
    [InlineData(StremioMediaType.Series, "tt0903747", null, "4c9c1c88-1f4c-a1f7-e20b-da196a397d28")]
    [InlineData(
        StremioMediaType.Series,
        "tt0903747:1:2",
        null,
        "650580bf-e5d3-4d21-47c9-354edef0f67d"
    )]
    [InlineData(
        StremioMediaType.Movie,
        "tt0111161",
        "somestream",
        "c4165978-24c6-fadf-e431-2668988a9c5f"
    )]
    public void ToGuid_IsStableForKnownInputs(
        StremioMediaType type,
        string externalId,
        string? streamId,
        string expected
    )
    {
        var uri = new StremioUri(type, externalId, streamId);

        Assert.Equal(Guid.Parse(expected), uri.ToGuid());
    }

    [Fact]
    public void FromBaseItem_Movie_PrefersImdb()
    {
        var movie = new Movie();
        movie.SetProviderId("Stremio", "kitsu:1");
        movie.SetProviderId(MetadataProvider.Imdb, "tt0111161");

        Assert.Equal("stremio://movie/tt0111161", StremioUri.FromBaseItem(movie)?.ToString());
    }

    [Fact]
    public void FromBaseItem_Movie_FallsBackToStremioProviderId()
    {
        var movie = new Movie();
        movie.SetProviderId("Stremio", "kitsu:1");

        Assert.Equal("stremio://movie/kitsu:1", StremioUri.FromBaseItem(movie)?.ToString());
    }

    [Fact]
    public void FromBaseItem_Movie_WithNoIds_ReturnsNull()
    {
        Assert.Null(StremioUri.FromBaseItem(new Movie()));
    }

    [Fact]
    public void FromBaseItem_Series_UsesImdb()
    {
        var series = new Series();
        series.SetProviderId(MetadataProvider.Imdb, "tt0903747");

        Assert.Equal("stremio://series/tt0903747", StremioUri.FromBaseItem(series)?.ToString());
    }

    [Fact]
    public void FromBaseItem_UnsupportedKind_Throws()
    {
        Assert.Throws<NotSupportedException>(() => StremioUri.FromBaseItem(new Audio()));
    }

    [Fact]
    public void FromBaseItem_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => StremioUri.FromBaseItem(null!));
    }
}
```

- [ ] **Step 2: Write the episode tests (these need the LibraryManager static)**

`Episode.Series` resolves through the process-wide `BaseItem.LibraryManager`. Keep these in their own class in a non-parallel collection so no other test observes a half-configured static.

Create `Gelato.Tests/Common/StremioUriFromEpisodeTests.cs`:

```csharp
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using NSubstitute;

namespace Gelato.Tests.Common;

[Collection("BaseItem statics")]
public sealed class StremioUriFromEpisodeTests : IDisposable
{
    private readonly ILibraryManager _previous = BaseItem.LibraryManager;
    private readonly ILibraryManager _library = Substitute.For<ILibraryManager>();

    public StremioUriFromEpisodeTests()
    {
        BaseItem.LibraryManager = _library;
    }

    public void Dispose()
    {
        BaseItem.LibraryManager = _previous;
    }

    [Fact]
    public void Episode_WithSeriesImdbAndNumbers_BuildsSeasonEpisodeId()
    {
        var series = SeriesWithImdb("tt0903747");
        var episode = new Episode
        {
            SeriesId = series.Id,
            ParentIndexNumber = 1,
            IndexNumber = 2,
        };

        Assert.Equal(
            "stremio://series/tt0903747:1:2",
            StremioUri.FromBaseItem(episode)?.ToString()
        );
    }

    [Fact]
    public void Episode_MissingEpisodeNumber_FallsBackToStremioProviderId()
    {
        var series = SeriesWithImdb("tt0903747");
        var episode = new Episode { SeriesId = series.Id, ParentIndexNumber = 1 };
        episode.SetProviderId("Stremio", "tt0903747:1:2");

        Assert.Equal(
            "stremio://series/tt0903747:1:2",
            StremioUri.FromBaseItem(episode)?.ToString()
        );
    }

    [Fact]
    public void Episode_MissingSeasonNumber_AndNoStremioId_ReturnsNull()
    {
        var series = SeriesWithImdb("tt0903747");
        var episode = new Episode { SeriesId = series.Id, IndexNumber = 2 };

        Assert.Null(StremioUri.FromBaseItem(episode));
    }

    [Fact]
    public void Episode_WithoutSeries_ReturnsNull()
    {
        // SeriesId and ParentId are both empty, so Episode.Series is null without any lookup.
        var episode = new Episode { ParentIndexNumber = 1, IndexNumber = 2 };

        Assert.Null(StremioUri.FromBaseItem(episode));
    }

    private Series SeriesWithImdb(string imdb)
    {
        var series = new Series { Id = Guid.NewGuid() };
        series.SetProviderId(MetadataProvider.Imdb, imdb);
        _library.GetItemById(series.Id).Returns(series);
        return series;
    }
}
```

- [ ] **Step 3: Run**

```bash
dotnet test Gelato.sln --filter "FullyQualifiedName~StremioUri"
```

Expected: 15 passed. Two things that can go wrong, and what they mean:

- `ToGuid_IsStableForKnownInputs` fails → the derivation changed or the expectations were mistyped. Re-derive by hand: `new Guid(MD5.HashData(Encoding.UTF8.GetBytes(uri.ToString())))`. Do not silently update the expected values.
- `Episode_WithSeriesImdbAndNumbers_BuildsSeasonEpisodeId` gets `null` → the substitute isn't being hit. Confirm `Episode.Series` in the referenced Jellyfin version still calls the non-generic `GetItemById(Guid)`; the NSubstitute setup targets that overload.

- [ ] **Step 4: Format and commit**

```bash
dotnet csharpier format Gelato.Tests/
dotnet format Gelato.sln --verify-no-changes
git add Gelato.Tests/Common/StremioUriTests.cs Gelato.Tests/Common/StremioUriFromEpisodeTests.cs
git commit -F - <<'EOF'
test: pin StremioUri GUID derivation and cover FromBaseItem

ToGuid() is the identity of every Gelato item in a Jellyfin library.
Pinning its output for known inputs makes an accidental change to the
derivation a build failure instead of a silent library wipe.

FromBaseItem is covered for movie, series and episode including the
fallback paths when IMDB, season or episode numbers are absent.

Claude-Session: https://claude.ai/code/session_01BpSFNaJzUKvd4QEVgTyyt4
EOF
```

---

### Task 5: ParseToTicks characterisation

**Files:**
- Create: `Gelato.Tests/Common/UtilsParseToTicksTests.cs`

**Interfaces:**
- Consumes: `Gelato.Utils.ParseToTicks(string?) : long?` (existing).

- [ ] **Step 1: Write the tests**

The expected values come from Findings §5 — they were produced by running the real method. Two rows document defects; their test names say so, and the comment explains the mechanism so the next person doesn't "fix" the test instead of the code.

Create `Gelato.Tests/Common/UtilsParseToTicksTests.cs`:

```csharp
namespace Gelato.Tests.Common;

public class UtilsParseToTicksTests
{
    [Theory]
    [InlineData("2:29:00", 89400000000L)]
    [InlineData("02:29:00", 89400000000L)]
    [InlineData("2h29min", 89400000000L)]
    [InlineData("2h 29min", 89400000000L)]
    [InlineData("1h", 36000000000L)]
    [InlineData("90s", 900000000L)]
    [InlineData("45sec", 450000000L)]
    [InlineData("PT90S", 900000000L)]
    [InlineData("1.02:03:04", 937840000000L)]
    public void ParsesSupportedFormats(string input, long expectedTicks)
    {
        Assert.Equal(expectedTicks, Utils.ParseToTicks(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankInput_ReturnsNull(string? input)
    {
        Assert.Null(Utils.ParseToTicks(input));
    }

    [Fact]
    public void Unparseable_ReturnsZeroNotNull()
    {
        // Every strategy fails, and the final regex fallback yields TimeSpan.Zero.
        Assert.Equal(0L, Utils.ParseToTicks("abc"));
    }

    /// <summary>
    /// KNOWN DEFECT, pinned deliberately. The code intends a bare number to mean minutes
    /// (see the `onlyNum` branch), but TimeSpan.TryParse accepts a bare integer as DAYS and
    /// returns first, so that branch is unreachable. "149" therefore parses as 149 days.
    /// Fixing this is a behaviour change; when it is fixed, update this expectation to
    /// 149 minutes (89400000000) in the same commit.
    /// </summary>
    [Fact]
    public void BareNumber_IsCurrentlyParsedAsDays_KnownDefect()
    {
        Assert.Equal(TimeSpan.FromDays(149).Ticks, Utils.ParseToTicks("149"));
    }

    /// <summary>
    /// KNOWN DEFECT, pinned deliberately. Input is lower-cased before XmlConvert.ToTimeSpan,
    /// which is case-sensitive and rejects "pt2h29m". The regex fallback then matches "2h"
    /// but not "29m" (it requires "min"), so the minutes are lost. "PT90S" survives only
    /// because the seconds regex accepts a bare "s".
    /// </summary>
    [Fact]
    public void Iso8601WithMinutes_LosesMinutes_KnownDefect()
    {
        Assert.Equal(TimeSpan.FromHours(2).Ticks, Utils.ParseToTicks("PT2H29M"));
    }
}
```

- [ ] **Step 2: Run**

```bash
dotnet test Gelato.sln --filter "FullyQualifiedName~UtilsParseToTicksTests"
```

Expected: 14 passed. If either `*_KnownDefect` test fails, someone fixed the defect — good — and the expectation must be updated to the correct value as described in each test's comment.

- [ ] **Step 3: Format and commit**

```bash
dotnet csharpier format Gelato.Tests/
dotnet format Gelato.sln --verify-no-changes
git add Gelato.Tests/Common/UtilsParseToTicksTests.cs
git commit -F - <<'EOF'
test: characterise Utils.ParseToTicks including two known defects

Pins every parsing strategy to values produced by the current
implementation. Two cases are documented as defects rather than fixed
here, because changing them alters runtime metadata for existing items:
a bare number parses as days (TimeSpan.TryParse wins before the
minutes branch), and ISO-8601 input loses its minutes after being
lower-cased.

Claude-Session: https://claude.ai/code/session_01BpSFNaJzUKvd4QEVgTyyt4
EOF
```

---

### Task 6: Enum mapping, IsUrl, KeyLock

**Files:**
- Create: `Gelato.Tests/Common/EnumMappingTests.cs`
- Create: `Gelato.Tests/Common/StringExtensionsTests.cs`
- Create: `Gelato.Tests/Common/KeyLockTests.cs`

**Interfaces:**
- Consumes: `Gelato.EnumMappingExtensions`, `Gelato.StringExtensions`, `Gelato.KeyLock` (existing), `Jellyfin.Data.Enums.BaseItemKind`.

- [ ] **Step 1: Write the enum and string tests**

Create `Gelato.Tests/Common/EnumMappingTests.cs`:

```csharp
using Jellyfin.Data.Enums;

namespace Gelato.Tests.Common;

public class EnumMappingTests
{
    [Theory]
    [InlineData(BaseItemKind.Movie, StremioMediaType.Movie)]
    [InlineData(BaseItemKind.Series, StremioMediaType.Series)]
    [InlineData(BaseItemKind.Season, StremioMediaType.Series)]
    [InlineData(BaseItemKind.Episode, StremioMediaType.Series)]
    [InlineData(BaseItemKind.Audio, StremioMediaType.Unknown)]
    [InlineData(BaseItemKind.Folder, StremioMediaType.Unknown)]
    public void ToStremio_MapsKinds(BaseItemKind kind, StremioMediaType expected)
    {
        Assert.Equal(expected, kind.ToStremio());
    }

    [Theory]
    [InlineData(StremioMediaType.Movie, BaseItemKind.Movie)]
    [InlineData(StremioMediaType.Series, BaseItemKind.Series)]
    public void ToBaseItem_MapsSupportedTypes(StremioMediaType type, BaseItemKind expected)
    {
        Assert.Equal(expected, type.ToBaseItem());
    }

    [Theory]
    [InlineData(StremioMediaType.Unknown)]
    [InlineData(StremioMediaType.Episode)]
    [InlineData(StremioMediaType.Channel)]
    public void ToBaseItem_RejectsUnsupportedTypes(StremioMediaType type)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => type.ToBaseItem());
    }
}
```

Create `Gelato.Tests/Common/StringExtensionsTests.cs`:

```csharp
namespace Gelato.Tests.Common;

public class StringExtensionsTests
{
    [Theory]
    [InlineData("http://example.com", true)]
    [InlineData("https://example.com/path", true)]
    [InlineData("HTTPS://EXAMPLE.COM", true)]
    [InlineData("ftp://example.com", false)]
    [InlineData("example.com", false)]
    [InlineData("", false)]
    public void IsUrl_AcceptsOnlyHttpSchemes(string input, bool expected)
    {
        Assert.Equal(expected, input.IsUrl());
    }
}
```

- [ ] **Step 2: Write the KeyLock tests**

`RunSingleFlightAsync` must coalesce concurrent callers with the same key onto one execution; `RunQueuedAsync` must serialise them. Both are used to stop duplicate Stremio fetches and duplicate inserts.

Create `Gelato.Tests/Common/KeyLockTests.cs`:

```csharp
namespace Gelato.Tests.Common;

public class KeyLockTests
{
    [Fact]
    public async Task SingleFlight_SameKey_RunsActionOnceAndSharesTheTask()
    {
        var keyLock = new KeyLock();
        var key = Guid.NewGuid();
        var gate = new TaskCompletionSource();
        var runs = 0;

        Task Action(CancellationToken _)
        {
            Interlocked.Increment(ref runs);
            return gate.Task;
        }

        var first = keyLock.RunSingleFlightAsync(key, Action);
        var second = keyLock.RunSingleFlightAsync(key, Action);

        Assert.Same(first, second);
        gate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task SingleFlight_AfterCompletion_RunsAgain()
    {
        var keyLock = new KeyLock();
        var key = Guid.NewGuid();
        var runs = 0;

        Task Action(CancellationToken _)
        {
            Interlocked.Increment(ref runs);
            return Task.CompletedTask;
        }

        await keyLock.RunSingleFlightAsync(key, Action);
        await keyLock.RunSingleFlightAsync(key, Action);

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task SingleFlight_DifferentKeys_RunIndependently()
    {
        var keyLock = new KeyLock();
        var runs = 0;

        Task Action(CancellationToken _)
        {
            Interlocked.Increment(ref runs);
            return Task.CompletedTask;
        }

        await Task.WhenAll(
            keyLock.RunSingleFlightAsync(Guid.NewGuid(), Action),
            keyLock.RunSingleFlightAsync(Guid.NewGuid(), Action)
        );

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task Queued_SameKey_NeverOverlaps()
    {
        var keyLock = new KeyLock();
        var key = Guid.NewGuid();
        var inside = 0;
        var maxInside = 0;
        var sync = new object();

        async Task Action(CancellationToken _)
        {
            var now = Interlocked.Increment(ref inside);
            lock (sync)
            {
                maxInside = Math.Max(maxInside, now);
            }
            await Task.Delay(15);
            Interlocked.Decrement(ref inside);
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => keyLock.RunQueuedAsync(key, Action)));

        Assert.Equal(1, maxInside);
    }

    [Fact]
    public async Task Queued_ReleasesLock_WhenActionThrows()
    {
        var keyLock = new KeyLock();
        var key = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            keyLock.RunQueuedAsync(key, _ => throw new InvalidOperationException("boom"))
        );

        // If the semaphore leaked, this would hang; the timeout turns a hang into a failure.
        var second = keyLock.RunQueuedAsync(key, _ => Task.CompletedTask);
        var completed = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(second, completed);
    }
}
```

- [ ] **Step 3: Run**

```bash
dotnet test Gelato.sln --filter "FullyQualifiedName~EnumMappingTests|FullyQualifiedName~StringExtensionsTests|FullyQualifiedName~KeyLockTests"
```

Expected: 22 passed (11 enum rows, 6 string rows, 5 KeyLock). `Queued_SameKey_NeverOverlaps` failing with `maxInside == 2` would mean the lock is broken, not the test.

- [ ] **Step 4: Run the whole suite once, then format and commit**

```bash
dotnet test Gelato.sln
dotnet csharpier format Gelato.Tests/
dotnet format Gelato.sln --verify-no-changes
git add Gelato.Tests/Common/EnumMappingTests.cs Gelato.Tests/Common/StringExtensionsTests.cs Gelato.Tests/Common/KeyLockTests.cs
git commit -F - <<'EOF'
test: cover enum mapping, IsUrl and KeyLock semantics

KeyLock guards against duplicate Stremio fetches and inserts; its
single-flight and queued behaviours are pinned including lock release
on exception.

Claude-Session: https://claude.ai/code/session_01BpSFNaJzUKvd4QEVgTyyt4
EOF
```

Expected total at this point: 1 + 7 + 4 + 15 + 14 + 22 = **63 tests passing**.

---

### Task 7: Package verification script

**Files:**
- Create: `scripts/verify-package.sh`

**Interfaces:**
- Produces: `scripts/verify-package.sh <plugin.zip> <version>` — exit 0 on success, exit 1 with `::error::verify-package: <reason>` on the first failed assertion. `<version>` may carry a leading `v`. Task 10 calls it exactly this way.

- [ ] **Step 1: Write the script**

Must run under macOS `/bin/bash` 3.2 as well as Ubuntu's bash 5 — so no `mapfile`, no `${var,,}`, BSD-compatible `sed`.

Create `scripts/verify-package.sh`:

```bash
#!/usr/bin/env bash
# Verify that a jprm-built plugin zip is consistent with build.yaml and carries the
# version we intend to release. This is the last check before anything is published.
#
# Usage: scripts/verify-package.sh <plugin.zip> <version>
#   <version> may be given with or without a leading "v".
set -euo pipefail

zip="${1:?usage: verify-package.sh <plugin.zip> <version>}"
expected_version="${2:?usage: verify-package.sh <plugin.zip> <version>}"
expected_version="${expected_version#v}"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
manifest="$root/build.yaml"

fail() {
  echo "::error::verify-package: $*" >&2
  exit 1
}

[ -f "$zip" ] || fail "zip not found: $zip"
[ -f "$manifest" ] || fail "build.yaml not found at $manifest"
command -v unzip >/dev/null || fail "unzip is required"
command -v python3 >/dev/null || fail "python3 is required"

# Scalars are `key: "value"` lines; the artifacts block is `  - "file.dll"` lines.
yaml_scalar() {
  sed -n "s/^$1: *\"\{0,1\}\([^\"]*\)\"\{0,1\} *\$/\1/p" "$manifest" | head -1
}
yaml_artifacts() {
  awk '
    /^artifacts:/ { in_list = 1; next }
    in_list && /^[[:space:]]+-[[:space:]]*/ {
      sub(/^[[:space:]]+-[[:space:]]*"?/, ""); sub(/"?[[:space:]]*$/, ""); print; next
    }
    in_list { in_list = 0 }
  ' "$manifest"
}

name="$(yaml_scalar name)"
guid="$(yaml_scalar guid)"
abi="$(yaml_scalar targetAbi)"
[ -n "$name" ] || fail "build.yaml has no name"
[ -n "$guid" ] || fail "build.yaml has no guid"
[ -n "$abi" ] || fail "build.yaml has no targetAbi"

listing="$(unzip -Z1 "$zip")"

artifact_count=0
while IFS= read -r artifact; do
  [ -n "$artifact" ] || continue
  artifact_count=$((artifact_count + 1))
  grep -qx -- "$artifact" <<<"$listing" || fail "artifact listed in build.yaml is missing from zip: $artifact"
done <<<"$(yaml_artifacts)"
[ "$artifact_count" -gt 0 ] || fail "build.yaml lists no artifacts"

grep -qx -- "meta.json" <<<"$listing" || fail "meta.json missing from zip"
meta="$(unzip -p "$zip" meta.json)"

meta_get() {
  python3 -c 'import json, sys; print(json.load(sys.stdin).get(sys.argv[1], ""))' "$1" <<<"$meta"
}

meta_name="$(meta_get name)"
meta_guid="$(meta_get guid | tr '[:lower:]' '[:upper:]')"
meta_abi="$(meta_get targetAbi)"
meta_version="$(meta_get version)"

[ "$meta_name" = "$name" ] || fail "meta.json name '$meta_name' != build.yaml name '$name'"
[ "$meta_guid" = "$(tr '[:lower:]' '[:upper:]' <<<"$guid")" ] || fail "meta.json guid '$meta_guid' != build.yaml guid '$guid'"
[ "$meta_abi" = "$abi" ] || fail "meta.json targetAbi '$meta_abi' != build.yaml targetAbi '$abi'"
[ "$meta_version" = "$expected_version" ] || fail "meta.json version '$meta_version' != expected '$expected_version'"

dll_size="$(unzip -l "$zip" | awk '$4 == "Gelato.dll" { print $1 }')"
[ -n "$dll_size" ] || fail "Gelato.dll missing from zip"
[ "$dll_size" -gt 100000 ] || fail "Gelato.dll is implausibly small: ${dll_size} bytes"

echo "verify-package: OK — $(basename "$zip"), version ${expected_version}, ${artifact_count} artifacts, Gelato.dll ${dll_size} bytes"
```

```bash
chmod +x scripts/verify-package.sh
```

- [ ] **Step 2: Build a real package to test against**

jprm needs a Python environment. Use one outside the repo so nothing needs ignoring:

```bash
python3 -m venv "$HOME/.cache/jprm-venv"
"$HOME/.cache/jprm-venv/bin/pip" install -q jprm
"$HOME/.cache/jprm-venv/bin/jprm" --version
```

Expected: `Jellyfin Plugin Repository Manager, version 1.1.x`.

```bash
rm -rf /tmp/gelato-pkg && mkdir -p /tmp/gelato-pkg
"$HOME/.cache/jprm-venv/bin/jprm" plugin build . --output=/tmp/gelato-pkg --version=9.9.9.9 2>&1 | tail -2
ls /tmp/gelato-pkg
```

Expected: last line printed is the zip path; directory contains `chocolate-gelato_9.9.9.9.zip` (+ `.md5sum`, `.meta.json`).

- [ ] **Step 3: Positive check**

```bash
scripts/verify-package.sh /tmp/gelato-pkg/chocolate-gelato_9.9.9.9.zip 9.9.9.9
scripts/verify-package.sh /tmp/gelato-pkg/chocolate-gelato_9.9.9.9.zip v9.9.9.9
```

Expected, both times: `verify-package: OK — chocolate-gelato_9.9.9.9.zip, version 9.9.9.9, 14 artifacts, Gelato.dll NNNNNN bytes`, exit 0.

- [ ] **Step 4: Negative checks — each must fail with a specific message**

```bash
scripts/verify-package.sh /tmp/gelato-pkg/chocolate-gelato_9.9.9.9.zip 1.2.3.4; echo "exit=$?"
```
Expected: `::error::verify-package: meta.json version '9.9.9.9' != expected '1.2.3.4'`, `exit=1`.

```bash
cp /tmp/gelato-pkg/chocolate-gelato_9.9.9.9.zip /tmp/gelato-pkg/broken.zip
zip -qd /tmp/gelato-pkg/broken.zip Mono.Nat.dll
scripts/verify-package.sh /tmp/gelato-pkg/broken.zip 9.9.9.9; echo "exit=$?"
```
Expected: `::error::verify-package: artifact listed in build.yaml is missing from zip: Mono.Nat.dll`, `exit=1`.

```bash
scripts/verify-package.sh /tmp/does-not-exist.zip 9.9.9.9; echo "exit=$?"
```
Expected: `::error::verify-package: zip not found: /tmp/does-not-exist.zip`, `exit=1`.

- [ ] **Step 5: Undo jprm's side-effect and confirm the tree is clean**

jprm stamped `<Version>9.9.9.9</Version>` into `Gelato.csproj` (Finding 3).

```bash
git checkout -- Gelato.csproj
git status --short
```

Expected: only `?? scripts/` is listed. If `Gelato.csproj` still shows modified, stop and investigate before committing.

- [ ] **Step 6: Commit**

```bash
git add scripts/verify-package.sh
git commit -F - <<'EOF'
build: add scripts/verify-package.sh for post-build package checks

Asserts a jprm zip carries every artifact build.yaml lists, that its
meta.json agrees with build.yaml on name, guid and targetAbi, and that
its version is the one being released. Runs before anything is
published, so a structurally broken package is a red CI run instead of
an entry in repository.json.

Claude-Session: https://claude.ai/code/session_01BpSFNaJzUKvd4QEVgTyyt4
EOF
```

---

### Task 8: Makefile decomposition

**Files:**
- Modify: `Makefile` (replace entirely)

**Interfaces:**
- Produces: targets `version`, `notes`, `publish`, `release`, `prerelease`, `release-preview`, `test`. Variables `VERSION` (with leading `v`), `RELEASE_FLAGS`, `NOTES_FILE`. Task 10's workflow calls `make version`, `make test`, `make notes VERSION=…`, `make publish VERSION=… RELEASE_FLAGS=…`.
- Removes: the old `make test` meaning (release-notes preview). That is now `make release-preview`.

- [ ] **Step 1: Replace the Makefile**

```make
RELEASE_FLAGS ?=
NOTES_FILE ?= /tmp/release_notes.md

# Print the next version git-cliff would cut, e.g. v0.26.19.0. Stderr is silenced so the
# output can be captured directly: v="$(make --no-print-directory version)".
version:
	@git cliff --bumped-version 2>/dev/null

# Write the release notes for VERSION to NOTES_FILE.
notes:
	@test -n "$(VERSION)" || { echo "VERSION is required, e.g. make notes VERSION=v1.2.3.4" >&2; exit 1; }
	@git cliff --unreleased --tag $(VERSION) --strip all 2>/dev/null > $(NOTES_FILE)
	@echo "Release notes for $(VERSION) written to $(NOTES_FILE)"

# Bump build.yaml to VERSION, commit, push, and create the GitHub Release.
# This is the first step with permanent effects. Run tests and package verification first.
publish:
	@test -n "$(VERSION)" || { echo "VERSION is required, e.g. make publish VERSION=v1.2.3.4" >&2; exit 1; }
	@test -s "$(NOTES_FILE)" || { echo "$(NOTES_FILE) is missing or empty; run make notes VERSION=$(VERSION) first" >&2; exit 1; }
	sed -i.bak 's/^version: .*/version: "$(VERSION:v%=%)"/' build.yaml && rm -f build.yaml.bak
	git add build.yaml
	git commit -m "chore(release): bump version to $(VERSION)"
	git push
	gh release create $(VERSION) --title "$(VERSION)" --notes-file $(NOTES_FILE) $(RELEASE_FLAGS)
	@echo "Release $(VERSION) created successfully!"

# Local end-to-end release: compute the version, write notes, publish.
# The CI workflow does NOT use this target; it runs tests and package checks between
# `version` and `publish`.
release:
	@set -e; \
	git fetch --tags; \
	v="$$(git cliff --bumped-version 2>/dev/null)"; \
	echo "New version will be: $$v"; \
	$(MAKE) --no-print-directory notes VERSION=$$v; \
	$(MAKE) --no-print-directory publish VERSION=$$v RELEASE_FLAGS="$(RELEASE_FLAGS)"

prerelease: RELEASE_FLAGS := --prerelease
prerelease: release

# Show the version and notes `make release` would produce, without changing anything.
release-preview:
	@set -e; \
	git fetch --tags; \
	v="$$(git cliff --bumped-version 2>/dev/null)"; \
	echo "New version will be: $$v"; \
	$(MAKE) --no-print-directory notes VERSION=$$v; \
	cat $(NOTES_FILE)

test:
	dotnet test Gelato.sln

.PHONY: version notes publish release prerelease release-preview test
```

- [ ] **Step 2: Verify the read-only targets**

```bash
make --no-print-directory version
```
Expected: a single line like `v0.26.19.0` and nothing else (no git-cliff INFO/WARN lines — they go to stderr, which is silenced).

```bash
make release-preview
```
Expected: `New version will be: v0.26.19.0`, `Release notes for v0.26.19.0 written to /tmp/release_notes.md`, then the notes body listing the `test:` and `build:` commits from this branch under their groups.

```bash
make test
```
Expected: `Passed! - Failed: 0, Passed: 63`.

- [ ] **Step 3: Verify `publish` refuses to run without its inputs (never run it for real)**

```bash
make publish; echo "exit=$?"
```
Expected: `VERSION is required, e.g. make publish VERSION=v1.2.3.4`, `exit=1`. Nothing is committed.

```bash
rm -f /tmp/release_notes.md
make publish VERSION=v9.9.9.9; echo "exit=$?"
```
Expected: `/tmp/release_notes.md is missing or empty; run make notes VERSION=v9.9.9.9 first`, `exit=1`. Nothing is committed.

```bash
git status --short
```
Expected: only `M Makefile`.

- [ ] **Step 4: Confirm the prerelease flag threads through (dry run)**

```bash
make -n publish VERSION=v9.9.9.9 RELEASE_FLAGS=--prerelease | grep "gh release create"
```
Expected: `gh release create v9.9.9.9 --title "v9.9.9.9" --notes-file /tmp/release_notes.md --prerelease`.

- [ ] **Step 5: Commit**

```bash
git add Makefile
git commit -F - <<'EOF'
build: split make release into version, notes and publish targets

`make release` bundled compute-bump-push-publish into one step, which
forced the CI workflow to publish before it could verify anything.
The pieces are now separate so the workflow can run tests and package
checks between computing the version and publishing it. `make release`
and `make prerelease` still work end to end for local use.

`make test` now runs the xUnit suite; the release-notes preview it used
to produce is `make release-preview`.

Claude-Session: https://claude.ai/code/session_01BpSFNaJzUKvd4QEVgTyyt4
EOF
```

---

### Task 9: Gate PRs and main on the test suite

**Files:**
- Modify: `.github/workflows/pr-build.yml`
- Modify: `.github/workflows/main.yml`

**Interfaces:**
- Consumes: `make test` from Task 8 (via `dotnet test Gelato.sln` directly, so the job doesn't depend on `make` being present).

- [ ] **Step 1: Add the test job to pr-build.yml**

Insert this job between `conventional-commits` and `build` in `.github/workflows/pr-build.yml`, matching the file's existing indentation (2 spaces):

```yaml
  test:
    runs-on: ubuntu-latest
    if: github.event.pull_request.draft == false
    steps:
      - name: Checkout repo
        uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.*"

      - name: Run tests
        run: dotnet test Gelato.sln --configuration Release
```

- [ ] **Step 2: Add the same job to main.yml**

Insert between `conventional-commits` and `build` in `.github/workflows/main.yml` — identical except there is no `if:` line, because push events have no draft state:

```yaml
  test:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout repo
        uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.*"

      - name: Run tests
        run: dotnet test Gelato.sln --configuration Release
```

- [ ] **Step 3: Validate the YAML parses and the job is registered**

```bash
python3 - <<'PY'
import yaml
for f in (".github/workflows/pr-build.yml", ".github/workflows/main.yml"):
    d = yaml.safe_load(open(f, encoding="utf-8-sig"))
    jobs = list(d["jobs"])
    assert "test" in jobs, f"{f}: no test job"
    print(f"OK   {f}  jobs={jobs}")
PY
```

Expected: both lines print `OK` with `jobs=['lint', 'conventional-commits', 'test', 'build']`. If `pyyaml` is missing: `python3 -m pip install --user pyyaml`.

- [ ] **Step 4: Commit and push the branch so the PR job proves itself**

```bash
git add .github/workflows/pr-build.yml .github/workflows/main.yml
git commit -F - <<'EOF'
ci: run the test suite on pull requests and pushes to main

Claude-Session: https://claude.ai/code/session_01BpSFNaJzUKvd4QEVgTyyt4
EOF
git push -u origin feat/test-suite-and-release-gate
gh pr create --title "feat: test suite and release gate" --body "Implements docs/superpowers/specs/2026-09-14-test-suite-and-release-gate-design.md. See docs/superpowers/plans/2026-09-14-test-suite-and-release-gate.md.

https://claude.ai/code/session_01BpSFNaJzUKvd4QEVgTyyt4"
```

- [ ] **Step 5: Watch the PR checks**

```bash
gh pr checks --watch
```

Expected: `lint`, `conventional-commits`, `test`, and `build / build` all pass. `test` must show 63 passed in its log (`gh run view --log` on the run id if needed). Do not merge yet — Task 10 changes `release.yml` on the same branch.

---

### Task 10: Reorder release.yml and prove it end to end

**Files:**
- Modify: `.github/workflows/release.yml` (replace entirely)

**Interfaces:**
- Consumes: `make version`, `make notes`, `make publish` (Task 8); `scripts/verify-package.sh` (Task 7); the jprm action's `version` input and `artifact` output.

- [ ] **Step 1: Replace release.yml**

```yaml
name: Release

on:
  workflow_dispatch:
    inputs:
      prerelease:
        description: "Cut as a prerelease (builds and attaches assets, but does not update repository.json)"
        type: boolean
        default: false

# Everything runs in one job under GITHUB_TOKEN. GitHub does not trigger workflows from
# events created with GITHUB_TOKEN, so a release cut here would never fire publish.yml;
# rather than keep a PAT alive to defeat that guard, this workflow does the whole job.
#
# Order matters: nothing is committed, pushed, tagged or released until the tests and
# the package verification have passed against the exact version being cut. A failure
# anywhere before "Publish release" leaves the repository untouched.
#
# publish.yml still handles the other path: a human promoting a prerelease to a full
# release is a human-originated event and fires normally.

permissions:
  contents: write

jobs:
  release:
    runs-on: ubuntu-latest

    steps:
      - name: Checkout code
        uses: actions/checkout@v4
        with:
          fetch-depth: 0

      - name: Configure Git
        run: |
          git config user.name "github-actions[bot]"
          git config user.email "41898282+github-actions[bot]@users.noreply.github.com"

      - name: Set up git-cliff
        uses: kenji-miyake/setup-git-cliff@v1

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.*"

      # ---- Verification: nothing below this line has side effects until "Publish release"

      - name: Resolve next version
        id: version
        run: |
          set -euo pipefail
          git fetch --tags
          tag="$(make --no-print-directory version)"
          if [ -z "$tag" ]; then
            echo "::error::git-cliff produced no version"
            exit 1
          fi
          echo "tag=${tag}" >> "$GITHUB_OUTPUT"
          echo "version=${tag#v}" >> "$GITHUB_OUTPUT"
          echo "Next version: ${tag}"

      - name: Run tests
        run: dotnet test Gelato.sln --configuration Release

      - name: Build plugin
        id: jprm
        uses: oddstr13/jellyfin-plugin-repository-manager@v1.1.1
        with:
          dotnet-target: "net10.0"
          version: ${{ steps.version.outputs.version }}

      - name: Verify package
        run: scripts/verify-package.sh "${{ steps.jprm.outputs.artifact }}" "${{ steps.version.outputs.version }}"

      # jprm stamps <Version> into Gelato.csproj while building. Put the tree back so the
      # release commit contains only the build.yaml bump.
      - name: Restore working tree after build
        run: |
          git checkout -- Gelato.csproj
          if [ -n "$(git status --porcelain --untracked-files=no)" ]; then
            echo "::error::working tree has unexpected tracked changes after build:"
            git status --porcelain --untracked-files=no
            exit 1
          fi

      - name: Prepare release assets
        id: assets
        run: |
          set -euo pipefail
          staging="$(mktemp -d)"
          cp "${{ steps.jprm.outputs.artifact }}" "$staging/"
          cd "$staging"
          for file in ./*.zip; do
            base="${file#./}"
            md5sum "$base" > "${base%.zip}.md5"
            sha256sum "$base" > "${base%.zip}.sha256"
          done
          ls -l
          echo "dir=${staging}" >> "$GITHUB_OUTPUT"

      # ---- Publication: first permanent effects

      - name: Write release notes
        run: make --no-print-directory notes VERSION="${{ steps.version.outputs.tag }}"

      - name: Publish release
        run: make --no-print-directory publish VERSION="${{ steps.version.outputs.tag }}" RELEASE_FLAGS="${{ inputs.prerelease && '--prerelease' || '' }}"
        env:
          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}

      - name: Upload release assets
        run: gh release upload "${{ steps.version.outputs.tag }}" "${{ steps.assets.outputs.dir }}"/* --clobber
        env:
          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}

      # Prereleases stop here: installable by hand, never advertised to Jellyfin servers.
      - name: Regenerate plugin manifest
        if: ${{ !inputs.prerelease }}
        uses: Kevinjil/jellyfin-plugin-repo-action@v0.4.3
        with:
          githubToken: ${{ secrets.GITHUB_TOKEN }}
          repository: ${{ github.repository }}
```

- [ ] **Step 2: Validate and commit**

```bash
python3 - <<'PY'
import yaml
d = yaml.safe_load(open(".github/workflows/release.yml", encoding="utf-8-sig"))
names = [s["name"] for s in d["jobs"]["release"]["steps"]]
print(names)
assert names.index("Run tests") < names.index("Build plugin") < names.index("Verify package") < names.index("Publish release"), "verification must precede publication"
print("order OK")
PY
git add .github/workflows/release.yml
git commit -F - <<'EOF'
ci: verify before publishing in the release workflow

The release job now computes the next version, runs the test suite,
builds the package at that version and verifies it, and only then
bumps, commits, pushes and creates the GitHub Release. A failure in any
verification step leaves the repository untouched instead of leaving a
burned tag and an orphan release.

Claude-Session: https://claude.ai/code/session_01BpSFNaJzUKvd4QEVgTyyt4
EOF
git push
gh pr checks --watch
```

Expected: PR checks green again (the `test` job, `lint`, `conventional-commits`, `build / build`).

- [ ] **Step 3: Merge to main**

```bash
gh pr merge --squash --delete-branch --body "https://claude.ai/code/session_01BpSFNaJzUKvd4QEVgTyyt4"
git checkout main && git pull --ff-only
gh run list --workflow="Main Build" -L 1
```

Wait for the `Main Build` run on the merge commit; expected `completed success` with the `test` job present.

Note on `--squash`: it collapses this branch into one commit on `main`. That keeps `git cliff` from listing every `test:`/`build:` commit individually in the next release notes. The squash commit message must itself be conventional — `gh pr merge` uses the PR title, which is `feat: test suite and release gate`.

- [ ] **Step 4: Prove the release gate end to end with a prerelease**

```bash
gh workflow run "Release" -f prerelease=true
sleep 10
run_id="$(gh run list --workflow="Release" -L 1 --json databaseId --jq '.[0].databaseId')"
gh run watch "$run_id" --exit-status
gh run view "$run_id" --json conclusion,jobs --jq '{conclusion, steps: [.jobs[].steps[] | "\(.conclusion)\t\(.name)"]}'
```

Expected: `conclusion: success`, and steps in this order all `success` except the last which is `skipped`:

```
Resolve next version
Run tests
Build plugin
Verify package
Restore working tree after build
Prepare release assets
Write release notes
Publish release
Upload release assets
Regenerate plugin manifest   <- skipped
```

Then confirm the artefact, the checksum and that the manifest was untouched:

```bash
tag="$(gh release list -L 1 --json tagName --jq '.[0].tagName')"
gh release view "$tag" --json isPrerelease,assets --jq '{isPrerelease, assets: [.assets[].name]}'
curl -sSL -o /tmp/ci.zip "https://github.com/adamlippert/Gelato/releases/download/${tag}/chocolate-gelato_${tag#v}.zip"
scripts/verify-package.sh /tmp/ci.zip "$tag"
gh api "repos/adamlippert/Gelato/contents/repository.json?ref=gh-pages" --jq '.content' | tr -d '\n ' | base64 -d | python3 -c 'import json,sys; print([v["version"] for p in json.load(sys.stdin) for v in p["versions"]])'
```

Expected: `isPrerelease: true` with three assets; `verify-package: OK …`; the manifest's version list does **not** contain the new version (only `0.26.18.1` and anything promoted since).

- [ ] **Step 5: Sync local and report**

```bash
git pull --ff-only
git log --oneline -3
```

Expected: HEAD is `chore(release): bump version to <tag>` authored by github-actions[bot] on top of the squash merge.

Report to the user: the prerelease tag that was cut, the run URL, and that they may delete the prerelease (`gh release delete <tag> --cleanup-tag --yes`) if they don't want it, or promote it (`gh release edit <tag> --prerelease=false`) to publish.

---

## Self-review

**Spec coverage**

| Spec requirement | Task |
|---|---|
| xUnit project, `net10.0`, references `Gelato.csproj`, in `Gelato.sln` | 1 |
| GUID and name agree between `Plugin.cs` and `build.yaml` | 2 |
| Every artifact resolvable next to `Gelato.dll`, located from test output | 2 |
| `build.yaml` parses and has required keys | 2 |
| `targetAbi` matches `Jellyfin.Controller` after 4-part normalisation | 2 |
| `PluginConfiguration` XML round-trip | 3 |
| `ToGuid()` pinned to hardcoded values | 4 |
| `ToString` / `FromBaseItem` incl. missing IMDB / season / episode | 4 |
| `ParseToTicks` across all strategies, null/empty/garbage | 5 (expectations corrected per Findings 1–2) |
| Enum round-trips, `IsUrl`, `KeyLock` concurrency | 6 |
| `verify-package.sh` assertions and non-zero exit with message | 7 |
| Makefile: `version`, `notes`, `publish`, `release`, `prerelease`, `release-preview`, `test` | 8 |
| `pr-build.yml` and `main.yml` gain `test` job | 9 |
| `release.yml` reordered: verify before any mutation | 10 |
| Spec risk: build runs pre-bump with explicit version; verification compares to intended version | 7 (script takes version arg), 10 (passes `steps.version.outputs.version`) |

**Placeholder scan:** none — every code step is complete; expected values are concrete.

**Type consistency:** `BuildManifest.Load` / `RepoPaths.File` used in Task 2 as defined; `verify-package.sh <zip> <version>` used identically in Tasks 7 and 10; Makefile variable `VERSION` carries the `v` prefix in both Task 8 and Task 10 (`steps.version.outputs.tag`), and `publish` strips it via `$(VERSION:v%=%)`; the jprm action receives the bare version (`steps.version.outputs.version`).
