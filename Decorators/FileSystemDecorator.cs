#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators;

/// <summary>
/// Wraps <see cref="IFileSystem"/> so that Gelato‑managed paths (MoviePath / SeriesPath)
/// return virtual entries derived from the Jellyfin database instead of hitting disk.
/// Non‑Gelato paths pass straight through to the real filesystem.
/// </summary>
public sealed class FileSystemDecorator : IFileSystem
{
    private readonly IFileSystem _inner;
    private readonly IItemRepository _repo;
    private readonly ILogger<FileSystemDecorator> _log;

    public FileSystemDecorator(
        IFileSystem inner,
        IItemRepository repo,
        ILogger<FileSystemDecorator> log
    )
    {
        _inner = inner;
        _repo = repo;
        _log = log;
    }

    private static bool IsGelatoPath(string path)
    {
        var cfg = GelatoPlugin.Instance?.Configuration;
        if (cfg is null)
            return false;

        return IsSubPathOf(path, cfg.MoviePath) || IsSubPathOf(path, cfg.SeriesPath);
    }

    private static bool IsSubPathOf(string path, string basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
            return false;

        // Normalize trailing separators for reliable comparison
        var normalizedBase =
            basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedPath =
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return normalizedPath.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.TrimEnd(Path.DirectorySeparatorChar)
                == normalizedBase.TrimEnd(Path.DirectorySeparatorChar);
    }

    private static readonly BaseItemKind[] GelatoItemKinds = new[]
    {
        BaseItemKind.Movie,
        BaseItemKind.Series,
        BaseItemKind.Season,
        BaseItemKind.Episode,
        // BaseItemKind.CollectionFolder,
        BaseItemKind.Folder,
    };

    /// <summary>
    /// Query items from the DB whose Path starts with <paramref name="directory"/>.
    /// Returns only direct children (one level deep).
    /// </summary>
    private IReadOnlyList<BaseItem> GetChildItemsFromDb(string directory)
    {
        var normalizedDir = directory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar
        );

        IReadOnlyList<BaseItem> allItems;
        try
        {
            allItems = _repo.GetItemList(
                new InternalItemsQuery
                {
                    Recursive = true,
                    IsDeadPerson = true, // skip gelato filters
                    IncludeItemTypes = GelatoItemKinds,
                }
            );
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "Gelato: error querying items for virtual directory {Path}",
                directory
            );
            return Array.Empty<BaseItem>();
        }

        // _log.LogInformation("Gelato: GetChildItemsFromDb({Dir}) - total items from DB: {Total}", directory, allItems.Count);

        var results = new List<BaseItem>();
        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in allItems)
        {
            if (string.IsNullOrEmpty(item.Path))
                continue;

            var itemPath = item.Path.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            );

            // Must be a child of the directory
            if (
                !itemPath.StartsWith(
                    normalizedDir + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                continue;

            // Get the relative portion after the directory
            var relative = itemPath.Substring(normalizedDir.Length + 1);

            // Direct child: no more separators
            if (
                !relative.Contains(Path.DirectorySeparatorChar)
                && !relative.Contains(Path.AltDirectorySeparatorChar)
            )
            {
                results.Add(item);
            }
            else
            {
                // Intermediate directory — extract the first segment
                var firstSeg = relative.Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    2
                )[0];
                seenDirs.Add(Path.Combine(normalizedDir, firstSeg));
            }
        }

        // Create synthetic folder items for intermediate directories
        foreach (var dir in seenDirs)
        {
            if (results.Any(r => string.Equals(r.Path, dir, StringComparison.OrdinalIgnoreCase)))
                continue;

            results.Add(
                new Folder
                {
                    Path = dir,
                    Name = Path.GetFileName(dir),
                    DateModified = DateTime.UtcNow,
                    DateCreated = DateTime.UtcNow,
                }
            );
        }

        return results;
    }

    private static FileSystemMetadata ToMetadata(BaseItem item)
    {
        var isDir = item.IsFolder;
        var path = item.Path ?? string.Empty;

        return new FileSystemMetadata
        {
            FullName = path,
            Name = Path.GetFileName(path),
            Extension = isDir ? string.Empty : Path.GetExtension(path),
            IsDirectory = isDir,
            Exists = true,
            Length = 0,
            LastWriteTimeUtc = item.DateModified,
            CreationTimeUtc = item.DateCreated,
        };
    }

    private static FileSystemMetadata FakeDirectoryMeta(string path) =>
        new()
        {
            FullName = path,
            Name = Path.GetFileName(path),
            Extension = string.Empty,
            IsDirectory = true,
            Exists = true,
            Length = 0,
            LastWriteTimeUtc = DateTime.UtcNow,
            CreationTimeUtc = DateTime.UtcNow,
        };

    private static FileSystemMetadata FakeFileMeta(string path) =>
        new()
        {
            FullName = path,
            Name = Path.GetFileName(path),
            Extension = Path.GetExtension(path),
            IsDirectory = false,
            Exists = true,
            Length = 1,
            LastWriteTimeUtc = DateTime.UtcNow,
            CreationTimeUtc = DateTime.UtcNow,
        };

    // ── IFileSystem – entries listing ───────────────────────────────────

    public IEnumerable<FileSystemMetadata> GetFileSystemEntries(string path, bool recursive = false)
    {
        if (!IsGelatoPath(path))
            return _inner.GetFileSystemEntries(path, recursive);

        var children = GetChildItemsFromDb(path);
        //  _log.LogInformation("Gelato: virtual GetFileSystemEntries for {Path} → {Count} entries", path, children.Count);
        //foreach (var c in children)
        //   _log.LogInformation("  → {ItemPath} (IsFolder={IsFolder}, Type={Type})", c.Path, c.IsFolder, c.GetType().Name);
        return children.Select(ToMetadata);
    }

    public IEnumerable<FileSystemMetadata> GetFiles(string path, bool recursive = false)
    {
        if (!IsGelatoPath(path))
            return _inner.GetFiles(path, recursive);

        return GetChildItemsFromDb(path).Where(i => !i.IsFolder).Select(ToMetadata);
    }

    public IEnumerable<FileSystemMetadata> GetFiles(
        string path,
        IReadOnlyList<string>? extensions,
        bool enableCaseSensitiveExtensions,
        bool recursive
    )
    {
        if (!IsGelatoPath(path))
            return _inner.GetFiles(path, extensions, enableCaseSensitiveExtensions, recursive);

        var items = GetChildItemsFromDb(path).Where(i => !i.IsFolder).Select(ToMetadata);

        if (extensions is { Count: > 0 })
        {
            var cmp = enableCaseSensitiveExtensions
                ? StringComparer.Ordinal
                : StringComparer.OrdinalIgnoreCase;
            var extSet = new HashSet<string>(extensions, cmp);
            items = items.Where(f => extSet.Contains(f.Extension));
        }

        return items;
    }

    public IEnumerable<FileSystemMetadata> GetFiles(
        string path,
        string searchPattern,
        bool recursive
    )
    {
        if (!IsGelatoPath(path))
            return _inner.GetFiles(path, searchPattern, recursive);

        return GetChildItemsFromDb(path).Where(i => !i.IsFolder).Select(ToMetadata);
    }

    public IEnumerable<FileSystemMetadata> GetFiles(
        string path,
        string searchPattern,
        IReadOnlyList<string>? extensions,
        bool enableCaseSensitiveExtensions,
        bool recursive
    )
    {
        if (!IsGelatoPath(path))
            return _inner.GetFiles(
                path,
                searchPattern,
                extensions,
                enableCaseSensitiveExtensions,
                recursive
            );

        var items = GetChildItemsFromDb(path).Where(i => !i.IsFolder).Select(ToMetadata);

        if (extensions is { Count: > 0 })
        {
            var cmp = enableCaseSensitiveExtensions
                ? StringComparer.Ordinal
                : StringComparer.OrdinalIgnoreCase;
            var extSet = new HashSet<string>(extensions, cmp);
            items = items.Where(f => extSet.Contains(f.Extension));
        }

        return items;
    }

    public IEnumerable<FileSystemMetadata> GetDirectories(string path, bool recursive = false)
    {
        if (!IsGelatoPath(path))
            return _inner.GetDirectories(path, recursive);

        return GetChildItemsFromDb(path).Where(i => i.IsFolder).Select(ToMetadata);
    }

    public IEnumerable<string> GetFilePaths(string path, bool recursive = false)
    {
        if (!IsGelatoPath(path))
            return _inner.GetFilePaths(path, recursive);

        return GetChildItemsFromDb(path).Where(i => !i.IsFolder).Select(i => i.Path!);
    }

    public IEnumerable<string> GetFilePaths(
        string path,
        string[]? extensions,
        bool enableCaseSensitiveExtensions,
        bool recursive
    )
    {
        if (!IsGelatoPath(path))
            return _inner.GetFilePaths(path, extensions, enableCaseSensitiveExtensions, recursive);

        var items = GetChildItemsFromDb(path).Where(i => !i.IsFolder);

        if (extensions is { Length: > 0 })
        {
            var cmp = enableCaseSensitiveExtensions
                ? StringComparer.Ordinal
                : StringComparer.OrdinalIgnoreCase;
            var extSet = new HashSet<string>(extensions, cmp);
            items = items.Where(i => extSet.Contains(Path.GetExtension(i.Path)));
        }

        return items.Select(i => i.Path!);
    }

    public IEnumerable<string> GetDirectoryPaths(string path, bool recursive = false)
    {
        if (!IsGelatoPath(path))
            return _inner.GetDirectoryPaths(path, recursive);

        return GetChildItemsFromDb(path).Where(i => i.IsFolder).Select(i => i.Path!);
    }

    public IEnumerable<string> GetFileSystemEntryPaths(string path, bool recursive = false)
    {
        if (!IsGelatoPath(path))
            return _inner.GetFileSystemEntryPaths(path, recursive);

        var items = GetChildItemsFromDb(path);
        //_log.LogInformation("Gelato: virtual GetFileSystemEntryPaths for {Path} → {Count} entries", path, items.Count);
        return items.Select(i => i.Path!);
    }

    public FileSystemMetadata GetFileSystemInfo(string path)
    {
        if (!IsGelatoPath(path))
            return _inner.GetFileSystemInfo(path);

        // Check if it's a known item path
        IReadOnlyList<BaseItem> items;
        try
        {
            items = _repo.GetItemList(
                new InternalItemsQuery
                {
                    Path = path,
                    // does it need to be recursive?
                    Recursive = true,
                    IsDeadPerson = true,
                    IncludeItemTypes = GelatoItemKinds,
                }
            );
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Gelato: error querying item for {Path}", path);
            items = Array.Empty<BaseItem>();
        }

        if (items.Count > 0)
        {
            //  _log.LogInformation("Gelato: GetFileSystemInfo found item for {Path} (Type={Type})", path, items[0].GetType().Name);
            return ToMetadata(items[0]);
        }

        // Could be an intermediate directory
        var hasChildren = GetChildItemsFromDb(path).Count > 0;
        if (hasChildren)
        {
            //  _log.LogInformation("Gelato: GetFileSystemInfo returning fake dir for {Path} (has children)", path);
            return FakeDirectoryMeta(path);
        }

        if (path.EndsWith(".strm"))
        {
            _log.LogWarning("Gelato: GetFileSystemInfo returning Exists=false for {Path}", path);
        }
        return new FileSystemMetadata { FullName = path, Exists = false };
    }

    public FileSystemMetadata GetFileInfo(string path)
    {
        if (!IsGelatoPath(path))
            return _inner.GetFileInfo(path);

        IReadOnlyList<BaseItem> items;
        try
        {
            items = _repo.GetItemList(
                new InternalItemsQuery
                {
                    Path = path,
                    Recursive = true,
                    IsDeadPerson = true,
                    IncludeItemTypes = GelatoItemKinds,
                }
            );
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Gelato: error querying file for {Path}", path);
            items = Array.Empty<BaseItem>();
        }

        if (items.Count > 0 && !items[0].IsFolder)
            return ToMetadata(items[0]);

        return new FileSystemMetadata
        {
            FullName = path,
            Exists = false,
            IsDirectory = false,
        };
    }

    public FileSystemMetadata GetDirectoryInfo(string path)
    {
        if (!IsGelatoPath(path))
            return _inner.GetDirectoryInfo(path);

        IReadOnlyList<BaseItem> items;
        try
        {
            items = _repo.GetItemList(
                new InternalItemsQuery
                {
                    Path = path,
                    Recursive = true,
                    IsDeadPerson = true,
                    IncludeItemTypes = GelatoItemKinds,
                }
            );
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Gelato: error querying directory for {Path}", path);
            items = Array.Empty<BaseItem>();
        }

        if (items.Count > 0 && items[0].IsFolder)
            return ToMetadata(items[0]);

        // Intermediate directory
        var hasChildren = GetChildItemsFromDb(path).Count > 0;
        if (hasChildren)
            return FakeDirectoryMeta(path);

        return new FileSystemMetadata
        {
            FullName = path,
            Exists = false,
            IsDirectory = true,
        };
    }

    public bool DirectoryExists(string path)
    {
        if (!IsGelatoPath(path))
            return _inner.DirectoryExists(path);

        return GetChildItemsFromDb(path).Count > 0;
    }

    public bool FileExists(string path)
    {
        if (!IsGelatoPath(path))
            return _inner.FileExists(path);

        IReadOnlyList<BaseItem> items;
        try
        {
            items = _repo.GetItemList(
                new InternalItemsQuery
                {
                    Path = path,
                    Recursive = true,
                    IsDeadPerson = true,
                    IncludeItemTypes = GelatoItemKinds,
                }
            );
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Gelato: error checking file existence for {Path}", path);
            items = Array.Empty<BaseItem>();
        }

        return items.Count > 0 && !items[0].IsFolder;
    }

    public DateTime GetCreationTimeUtc(FileSystemMetadata info) => _inner.GetCreationTimeUtc(info);

    public DateTime GetCreationTimeUtc(string path)
    {
        if (!IsGelatoPath(path))
            return _inner.GetCreationTimeUtc(path);
        return DateTime.UtcNow;
    }

    public DateTime GetLastWriteTimeUtc(FileSystemMetadata info) =>
        _inner.GetLastWriteTimeUtc(info);

    public DateTime GetLastWriteTimeUtc(string path)
    {
        if (!IsGelatoPath(path))
            return _inner.GetLastWriteTimeUtc(path);
        return DateTime.UtcNow;
    }

    public bool IsShortcut(string filename) => _inner.IsShortcut(filename);

    public string? ResolveShortcut(string filename) => _inner.ResolveShortcut(filename);

    public void CreateShortcut(string shortcutPath, string target) =>
        _inner.CreateShortcut(shortcutPath, target);

    public string MakeAbsolutePath(string folderPath, string filePath) =>
        _inner.MakeAbsolutePath(folderPath, filePath);

    public void MoveDirectory(string source, string destination) =>
        _inner.MoveDirectory(source, destination);

    public string GetValidFilename(string filename) => _inner.GetValidFilename(filename);

    public void SwapFiles(string file1, string file2) => _inner.SwapFiles(file1, file2);

    public bool AreEqual(string path1, string path2) => _inner.AreEqual(path1, path2);

    public bool ContainsSubPath(string parentPath, string path) =>
        _inner.ContainsSubPath(parentPath, path);

    public string GetFileNameWithoutExtension(FileSystemMetadata info) =>
        _inner.GetFileNameWithoutExtension(info);

    public bool IsPathFile(string path) => _inner.IsPathFile(path);

    public void DeleteFile(string path) => _inner.DeleteFile(path);

    public IEnumerable<FileSystemMetadata> GetDrives() => _inner.GetDrives();

    public void SetHidden(string path, bool isHidden) => _inner.SetHidden(path, isHidden);

    public void SetAttributes(string path, bool isHidden, bool readOnly) =>
        _inner.SetAttributes(path, isHidden, readOnly);
}
