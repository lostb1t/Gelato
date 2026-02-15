#pragma warning disable CA1002, CA1819, CS1591

using System.Collections.Generic;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;

namespace Gelato.Decorators
{
    public class DirectoryServiceDecorator : IDirectoryService
    {
        private readonly IDirectoryService _inner;

        public DirectoryServiceDecorator(IDirectoryService inner)
        {
            _inner = inner;
        }

        public FileSystemMetadata[] GetFileSystemEntries(string path)
        {
            return _inner.GetFileSystemEntries(path);
        }

        public List<FileSystemMetadata> GetDirectories(string path)
        {
            return _inner.GetDirectories(path);
        }

        public List<FileSystemMetadata> GetFiles(string path)
        {
            return _inner.GetFiles(path);
        }

        public FileSystemMetadata? GetFile(string path)
        {
            return _inner.GetFile(path);
        }

        public FileSystemMetadata? GetDirectory(string path)
        {
            return _inner.GetDirectory(path);
        }

        public FileSystemMetadata? GetFileSystemEntry(string path)
        {
            return _inner.GetFileSystemEntry(path);
        }

        public IReadOnlyList<string> GetFilePaths(string path)
        {
            return _inner.GetFilePaths(path);
        }

        public IReadOnlyList<string> GetFilePaths(string path, bool clearCache, bool sort = false)
        {
            return _inner.GetFilePaths(path, clearCache, sort);
        }

        public bool IsAccessible(string path)
        {
            return _inner.IsAccessible(path);
        }
    }
}