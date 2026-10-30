using System;
using System.Collections.Generic;
using System.IO;

namespace RandomMediaPlayer
{
    /// <summary>
    /// Restricts random picks to one folder, optionally including its
    /// subfolders. Created from the folder of the file on screen when the user
    /// presses a stay-in-folder hotkey. The filtered file list is computed
    /// lazily on the worker thread and cached per source list instance, so a
    /// rescan (which swaps in a new list) refreshes it automatically.
    /// </summary>
    public sealed class FolderLock
    {
        public string Folder { get; }
        public bool IncludeSubfolders { get; }

        // Folder plus a trailing separator, so "C:\pics" never matches
        // "C:\pictures" in the subfolder test.
        private readonly string _prefix;
        private readonly object _gate = new();
        private List<string>? _source;
        private List<string>? _filtered;

        public FolderLock(string folder, bool includeSubfolders)
        {
            Folder = NormalizeFolder(folder);
            IncludeSubfolders = includeSubfolders;
            _prefix = Folder.EndsWith(Path.DirectorySeparatorChar) || Folder.EndsWith(Path.AltDirectorySeparatorChar)
                ? Folder
                : Folder + Path.DirectorySeparatorChar;
        }

        /// <summary>Normalized folder of the given file, or null if it has none.</summary>
        public static string? FolderOf(string? filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                return string.IsNullOrEmpty(dir) ? null : NormalizeFolder(dir);
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeFolder(string folder)
        {
            try { folder = Path.GetFullPath(folder); } catch { }
            // Keeps the separator on a bare drive root ("C:\"), drops it elsewhere.
            return Path.TrimEndingDirectorySeparator(folder);
        }

        public bool SameScopeAs(FolderLock other) =>
            IncludeSubfolders == other.IncludeSubfolders
            && string.Equals(Folder, other.Folder, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True when the file sits directly in the locked folder or, with
        /// IncludeSubfolders, anywhere beneath it.
        /// </summary>
        public bool Matches(string path)
        {
            var dir = Path.GetDirectoryName(path.AsSpan());
            if (dir.IsEmpty) return false;
            if (dir.Equals(Folder, StringComparison.OrdinalIgnoreCase)) return true;
            return IncludeSubfolders && dir.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The subset of <paramref name="all"/> inside the lock. Cached against
        /// the list instance, so repeated picks against the same scan are free.
        /// Safe to call from any thread.
        /// </summary>
        public List<string> Filter(List<string> all)
        {
            lock (_gate)
            {
                if (ReferenceEquals(all, _source) && _filtered != null) return _filtered;
            }

            var result = new List<string>();
            foreach (var p in all)
                if (Matches(p)) result.Add(p);

            lock (_gate)
            {
                _source = all;
                _filtered = result;
            }
            return result;
        }
    }
}
