using Gelato.Config;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

/// <summary>A Jellyfin library as the settings page offers it.</summary>
/// <param name="GelatoPath">The library's folder that is Gelato's, if it has one.</param>
public sealed record GelatoLibrary(
    string Id,
    string Name,
    string? CollectionType,
    string[] Locations,
    string? GelatoPath
);

/// <summary>
/// Lets the settings page pick a library instead of typing a path: Gelato gives every library it is
/// picked for a folder of its own under the base path and adds that folder to the library.
/// </summary>
/// <remarks>
/// One folder per library, not per catalog or user: everything picked for the same library shares it.
/// The path stays what the configuration stores, so the folder lookup does not change, and a path
/// set before the pickers keeps working as long as it is in a library.
/// </remarks>
public class LibraryFolderService(
    ILibraryManager libraryManager,
    ILibraryMonitor libraryMonitor,
    ILogger<LibraryFolderService> log
)
{
    private static readonly HashSet<string> ReservedNames =
    [
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    ];

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>The library kinds the settings page offers; one without a kind is mixed.</summary>
    private static readonly HashSet<string> GelatoKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "movies",
        "tvshows",
        "mixed",
    };

    private readonly Lock _ensureLock = new();

    /// <summary>The folder new library folders go into when no base path is set.</summary>
    public string GetDefaultBasePath() => GelatoPlugin.Instance!.Configuration.GetDefaultBasePath();

    public IReadOnlyList<GelatoLibrary> GetLibraries(string? basePath = null)
    {
        var cfg = GelatoPlugin.Instance!.Configuration;
        // Listing never fails over the base path: a search inside a library asks for this list,
        // and the settings page needs it to offer anything at all.
        var root = ResolveBasePath(basePath, strict: false);
        var configured = cfg.GetLibraryPaths().Select(p => Normalize(p.Path)).ToList();

        return libraryManager
            .GetVirtualFolders()
            .Select(v => new GelatoLibrary(
                v.ItemId,
                v.Name,
                v.CollectionType?.ToString(),
                v.Locations,
                v.Locations.FirstOrDefault(l => IsGelatoFolder(l, v.Name, root, configured))
            ))
            .ToList();
    }

    /// <summary>
    /// The library's Gelato folder: the one it has, or a new one under the base path, seeded, added to
    /// the library and scanned so Gelato finds it.
    /// </summary>
    public string EnsureFolder(string libraryId, string? basePath = null)
    {
        var root = ResolveBasePath(basePath, strict: true);

        // One at a time: two requests for the same library would both find it without a folder
        // and add the same path twice.
        lock (_ensureLock)
        {
            var library =
                GetLibraries(basePath)
                    .FirstOrDefault(l =>
                        string.Equals(l.Id, libraryId, StringComparison.OrdinalIgnoreCase)
                    )
                ?? throw new ArgumentException($"No library {libraryId}");

            // Gelato has movies and series: a music library, the collections or the playlists
            // get no folder, whatever asks for one.
            if (library.CollectionType is { } type && !GelatoKinds.Contains(type))
            {
                throw new NotSupportedException(
                    $"Library {library.Name} is a {type} library: Gelato needs a movies, shows or mixed library"
                );
            }

            if (library.GelatoPath is { } existing)
            {
                GelatoManager.SeedFolder(existing);
                return existing;
            }

            var path = FreeFolder(root, library.Name);

            GelatoManager.SeedFolder(path);
            libraryMonitor.Stop();
            try
            {
                libraryManager.AddMediaPath(library.Name, new MediaPathInfo(path));
            }
            finally
            {
                // The scan starts the monitor again once it is done.
                libraryManager.QueueLibraryScan();
            }

            log.LogInformation(
                "Added Gelato folder {Path} to library {Library}",
                path,
                library.Name
            );
            return path;
        }
    }

    /// <summary>
    /// A folder under the base path named after the library, with a number appended when the name is
    /// taken: by another library's location, or by a folder that holds anything but Gelato's seed
    /// file. A folder with media in it is never made Gelato's.
    /// </summary>
    private string FreeFolder(string root, string libraryName)
    {
        var name = FolderName(libraryName);

        var taken = libraryManager
            .GetVirtualFolders()
            .SelectMany(v => v.Locations)
            .Select(Normalize)
            .ToHashSet(
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal
            );

        bool Taken(string path) => taken.Contains(Normalize(path)) || HoldsOtherFiles(path);

        var path = Path.Combine(root, name);
        for (var i = 2; Taken(path); i++)
            path = Path.Combine(root, $"{name}-{i}");
        return path;
    }

    /// <summary>
    /// The library name as a folder name: lowercase letters and digits, anything else a single dash.
    /// "Anime & Cartoons" becomes "anime-cartoons", "Séries" becomes "series", "アニメ" stays. A name
    /// Windows reserves for a device ("con", "nul") gets a suffix.
    /// </summary>
    public static string FolderName(string libraryName)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in libraryName.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (
                System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                == System.Globalization.UnicodeCategory.NonSpacingMark
            )
                continue;

            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
        }

        var name = sb.ToString().TrimEnd('-');
        if (name.Length == 0)
            return "library";

        return ReservedNames.Contains(name) ? name + "-library" : name;
    }

    /// <summary>
    /// The base path as a full path: the one given or the configured one. What is typed ends up in
    /// the stored folder path, which Gelato looks its folder up by, so "C:/gelato" or a path with
    /// ".." in it is resolved here, and a relative one, which would depend on where the server was
    /// started, is refused.
    /// </summary>
    /// <param name="strict">
    /// Whether a relative path is refused, as it is before a folder is created under it. Without,
    /// the default base path stands in: the settings page saves what was typed, and a list of the
    /// libraries must not fail over it.
    /// </param>
    private static string ResolveBasePath(string? basePath, bool strict)
    {
        var cfg = GelatoPlugin.Instance!.Configuration;
        var root = string.IsNullOrWhiteSpace(basePath) ? cfg.GetBasePath() : basePath.Trim();
        if (!Path.IsPathFullyQualified(root))
        {
            if (strict)
                throw new IOException($"The Gelato folder must be a full path: {root}");

            root = cfg.GetDefaultBasePath();
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    /// <summary>
    /// Whether a library location is Gelato's: a folder the configuration names, one carrying Gelato's
    /// seed file, or an empty one under the base path that is named after the library (a folder Gelato
    /// created, whose seed file went with the last shutdown). A folder with anything else in it is
    /// never taken for Gelato's, and neither is an empty one of another name: with a base path among
    /// media folders that can be a mount point that is not mounted.
    /// </summary>
    private static bool IsGelatoFolder(
        string location,
        string libraryName,
        string root,
        List<string> configured
    )
    {
        var normalized = Normalize(location);
        if (
            configured.Any(c => string.Equals(c, normalized, PathComparison))
            || GelatoManager.IsSeedFile(Path.Combine(location, GelatoManager.SeedFileName))
        )
        {
            return true;
        }

        var name = FolderName(libraryName);
        var folder = Path.GetFileName(normalized);
        return string.Equals(Path.GetDirectoryName(normalized), root, PathComparison)
            && (folder == name || IsNumbered(folder, name))
            && !HoldsOtherFiles(location);
    }

    /// <summary>
    /// Whether the folder is the library's name with the number <see cref="FreeFolder"/> appends:
    /// "movies-2", not "movies-old" or "movies-4k", which are somebody else's folders.
    /// </summary>
    private static bool IsNumbered(string folder, string name)
    {
        var suffix = folder.AsSpan();
        if (!suffix.StartsWith(name + "-", StringComparison.Ordinal))
            return false;

        suffix = suffix[(name.Length + 1)..];
        return suffix.Length > 0 && !suffix.ContainsAnyExceptInRange('0', '9');
    }

    private static bool HoldsOtherFiles(string path)
    {
        try
        {
            return Directory.Exists(path)
                && Directory
                    .EnumerateFileSystemEntries(path)
                    .Any(e => !GelatoManager.IsSeedFile(e));
        }
        catch (Exception)
        {
            // Unreadable: not a folder to take over.
            return true;
        }
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        }
        catch (Exception)
        {
            return path.Trim();
        }
    }
}
