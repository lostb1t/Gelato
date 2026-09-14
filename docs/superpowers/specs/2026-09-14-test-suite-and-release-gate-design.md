# Test suite and release gate

**Date:** 2026-09-14
**Status:** Approved, not yet implemented
**Repo:** `adamlippert/Gelato` (Chocolate Gelato) — fork of `lostb1t/Gelato`

## Problem

The plugin has no tests. 11,530 lines across ~30 files, no test project, no
framework, no `dotnet test` in any workflow. The only tooling is csharpier,
a formatter.

The release pipeline can therefore publish a structurally broken plugin and
report success. Two hazards are live today:

1. The plugin GUID exists in **two** places — `Plugin.cs` and `build.yaml` —
   that must agree. Nothing enforces it. They were set by hand during the
   fork rebrand.
2. `release.yml` bumps the version, commits, pushes, and creates the GitHub
   Release **before** it builds. A build failure leaves a burned tag and an
   orphan release with no assets, which has already had to be repaired by
   hand once.

## Goal

Stop broken plugins reaching `repository.json`. Priority is a fast, reliable
CI gate over deep coverage. Secondary: unit coverage of the pure logic whose
silent drift would be most damaging.

Explicit non-goal: verifying the plugin works inside a running Jellyfin.

## Failure modes being targeted

For a Jellyfin plugin, "broken" almost never means a wrong return value. It
means the plugin fails to load, or loads and does nothing:

- an artifact DLL missing from `build.yaml`'s `artifacts:` list — the plugin
  dies on load with `FileNotFoundException`
- `Plugin.cs` and `build.yaml` GUIDs drifting apart — Jellyfin treats it as a
  different plugin, silently
- `targetAbi` not matching the Jellyfin assemblies actually built against
- `PluginConfiguration` failing to deserialize — plugin loads, config page 500s
- a manifest checksum not matching the published asset — Jellyfin refuses the
  install

## Design

### Two homes, split by what they need to run

| Component | Runs via | Requires |
|---|---|---|
| `Gelato.Tests/` (xUnit) | `dotnet test` | source only |
| `scripts/verify-package.sh` | CI, or manually | the built jprm `.zip` |

Zip inspection stays **out** of xUnit deliberately. Folding it in would make
`dotnet test` fail on a clean checkout unless a package had been built first;
a suite that fails for procedural reasons stops being run. xUnit must be
runnable anywhere, at any time, with no prerequisites.

### `Gelato.Tests`

Created with `dotnet new xunit` so package versions come from the installed
SDK rather than being pinned to numbers that may not exist. Targets `net10.0`,
references `Gelato.csproj`, added to `Gelato.sln`.

xUnit chosen because Jellyfin itself uses it — tests written here can travel
upstream in a PR without rewriting.

#### Consistency assertions

These encode the invariants that the fork rebrand created and that nothing
currently protects:

- `Plugin.cs` GUID equals `build.yaml` `guid`
- `Plugin.Name` equals `build.yaml` `name`
- every entry in `build.yaml` `artifacts:` is resolvable as a file alongside
  the referenced `Gelato.dll`. The test must locate that directory from the
  test assembly's own output (where project references are copied) rather than
  assuming `bin/Release/net10.0` — `dotnet test` builds Debug by default, so a
  hardcoded Release path would pass or fail for the wrong reason.
- `build.yaml` parses as YAML and carries the required keys
- `targetAbi` matches the `Jellyfin.Controller` version referenced in
  `Gelato.csproj`, **compared after normalising to four components**:
  `targetAbi` is `12.0.0.0` while the package version is `12.0.0`, so a naive
  string comparison always fails.
- `PluginConfiguration` round-trips through Jellyfin's XML serializer without
  loss or throw

The GUID and name assertions read `Plugin.cs` as text. This is deliberate: the
values are compile-time constants in two unrelated file formats, and a text
comparison is the honest way to compare them. If the pattern stops matching,
the test fails loudly, which is the correct outcome.

#### Pure logic

- **`StremioUri.ToGuid()` — pinned to hardcoded expected GUIDs.** The highest
  value test in the suite. This MD5-derives the Jellyfin item ID from a
  `stremio://` URI, so it is the identity of every Gelato item in a user's
  library. Silent drift orphans entire libraries. Locking known inputs to
  known outputs makes such a change impossible to introduce by accident.
- `StremioUri.ToString()` and `FromBaseItem` across movie, series and episode,
  including the fallback paths when IMDB id, `ParentIndexNumber` or
  `IndexNumber` are absent.
- `Utils.ParseToTicks` across all four strategies and their boundaries:
  `TimeSpan.TryParse` (`"2:29:00"`), ISO-8601 (`"PT90S"`), regex
  (`"2h29min"`), plus null, empty and unparseable input. Tests pin the
  behaviour the code actually has, established by running it. Two cases are
  pinned as **known defects** rather than fixed, since fixing them changes
  runtime metadata on existing items: a bare number (`"149"`) parses as 149
  *days* because `TimeSpan.TryParse` accepts it before the minutes branch is
  reached, and `"PT2H29M"` loses its minutes because the input is lower-cased
  before the case-sensitive `XmlConvert.ToTimeSpan`.
- `EnumMappingExtensions` round-trips, `StringExtensions.IsUrl`, and `KeyLock`
  under concurrent access.

### `scripts/verify-package.sh`

Takes a jprm-built zip and asserts:

- every `artifacts:` entry from `build.yaml` is present in the archive
- `meta.json` GUID, name and `targetAbi` match `build.yaml`
- `meta.json` version matches the version being released
- `Gelato.dll` is present and of plausible size

Exits non-zero with a specific message naming the failed assertion.

### Release pipeline reordering

Current order publishes before it verifies. New order:

```
compute next version  (git cliff --bumped-version, no commit)
   |
dotnet test                            <- fails here: repo untouched
   |
jprm build at that version
   |
scripts/verify-package.sh              <- fails here: repo untouched
   |
bump . commit . push . create release  <- first permanent effect
   |
upload assets -> regenerate manifest
```

A failing run leaves no tag, no orphan release and no pushed bump — only a red
run to fix and re-dispatch.

### Makefile decomposition

`make release` currently does compute-bump-push-publish as one indivisible
step, which is what forces publication ahead of verification. Split into:

- `make version` — print the next version
- `make notes VERSION=...` — write release notes
- `make publish VERSION=... [RELEASE_FLAGS=--prerelease]` — bump `build.yaml`,
  commit, push, create the GitHub Release
- `make release` / `make prerelease` — orchestrate the above; local behaviour
  unchanged

**Rename:** `make test` currently previews release notes. Once real tests
exist that name is actively misleading. It becomes `make release-preview`,
and `make test` runs `dotnet test`.

### Workflow changes

- `pr-build.yml` — add a `test` job running `dotnet test`
- `main.yml` — add a `test` job running `dotnet test`
- `release.yml` — reorder per above; add test and package-verification steps
  ahead of any mutating step

## Out of scope

No running Jellyfin instance, so these remain uncovered:

- DI registration failures at server startup
- decorator interception actually intercepting
- anything needing a real library database
- end-to-end install into Jellyfin

A green suite means "structurally sound, core logic correct" — **not**
"verified working in Jellyfin". Closing that gap needs a Dockerised Jellyfin 12
integration harness, considered and deliberately deferred.

## Risks

- **Text-scraping `Plugin.cs`** is coupled to its formatting. Mitigated by the
  failure being loud rather than silent.
- **Pinned `ToGuid()` expectations** will fail if the derivation ever changes
  intentionally. That is the point; the fix is to change the expected values
  in the same commit, deliberately.
- **Reordering `release.yml`** means the build runs against the pre-bump
  working tree with the version passed explicitly to jprm, rather than read
  from a committed `build.yaml`. The package verification step must therefore
  compare against the intended version, not the file on disk.
