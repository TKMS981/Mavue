using Mavue.Core.Formats;
using Mavue.Core.Viewing;

namespace Mavue.Core.Archives;

/// <summary>A file or folder shown in the archive listing.</summary>
/// <param name="Name">Display name (last segment; control and bidirectional characters replaced).</param>
/// <param name="Path">Normalized path inside the archive ("" for the root).</param>
/// <param name="IsFolder">A folder (declared by an entry or implied by entry paths).</param>
/// <param name="Entry">The entry (null for an implied folder).</param>
public sealed record ArchiveNode(string Name, string Path, bool IsFolder, ZipEntry? Entry)
{
    /// <summary>Entries directly inside this folder (folders only).</summary>
    public int ChildCount { get; internal set; }

    /// <summary>Listed at the root with its full name because the name is unsafe (never used as a path).</summary>
    public bool IsUnsafe => Entry is { NameCheck: not ArchiveNameCheck.Safe };

    /// <summary>A file the image sequence shows.</summary>
    public bool IsImage { get; internal set; }
}

/// <summary>Counts for the archive summary (declared values; nothing is decompressed to compute them).</summary>
public sealed record ArchiveSummary(
    int Files,
    int Folders,
    long DeclaredSize,
    long CompressedSize,
    int Images,
    int Encrypted,
    int UnsupportedMethod,
    int UnsafeNames,
    int SuspiciousRatio,
    int NestedArchives);

/// <summary>
/// The folder structure of an archive listing (one level is shown at a time) and the sequence of images it contains.
/// Folders come first, then files, both in natural order ("2" before "10"). Entries with unsafe names
/// (<see cref="ArchiveEntryName.Check"/>) are not placed in folders: they are listed at the root under their full
/// display name with a warning. Built once per archive, off the UI thread.
/// </summary>
public sealed class ArchiveTree
{
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".cbz", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".cab", ".jar", ".apk", ".iso",
    };

    private readonly Dictionary<string, List<ArchiveNode>> _children = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sorted = new(StringComparer.Ordinal);

    private ArchiveTree(ArchiveSummary summary, IReadOnlyList<ZipEntry> images)
    {
        Summary = summary;
        Images = images;
    }

    public ArchiveSummary Summary { get; private set; }

    /// <summary>Readable image entries in natural path order (the order of pages in a comic archive).</summary>
    public IReadOnlyList<ZipEntry> Images { get; private set; }

    /// <summary>Image formats shown from inside an archive: what Windows Imaging Component decodes (not SVG).</summary>
    public static bool IsImageName(string name)
    {
        FileFormat format = FileFormatDetector.FromExtension(System.IO.Path.GetExtension(name));
        return format != FileFormat.Svg && ViewerFormats.Images.Contains(format);
    }

    /// <summary>An archive inside the archive: listed, never opened.</summary>
    public static bool IsArchiveName(string name) => ArchiveExtensions.Contains(System.IO.Path.GetExtension(name));

    public static ArchiveTree Build(IReadOnlyList<ZipEntry> entries, ArchiveLimits? limits = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        limits ??= ArchiveLimits.Default;
        var tree = new ArchiveTree(new ArchiveSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0), []);
        var folders = new Dictionary<string, ArchiveNode>(StringComparer.Ordinal);
        var images = new List<ZipEntry>();
        int files = 0, encrypted = 0, unsupported = 0, unsafeNames = 0, suspicious = 0, nested = 0;
        long size = 0, compressed = 0;
        tree._children[string.Empty] = [];

        for (int i = 0; i < entries.Count; i++)
        {
            if ((i & 4095) == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            ZipEntry entry = entries[i];
            if (entry.NameCheck != ArchiveNameCheck.Safe)
            {
                unsafeNames++;
                if (!entry.IsDirectory)
                {
                    files++;
                    tree._children[string.Empty].Add(new ArchiveNode(ArchiveEntryName.ForDisplay(entry.Name), entry.Name, false, entry));
                }

                continue;
            }

            if (entry.IsDirectory)
            {
                EnsureFolder(tree, folders, entry.Path, entry);
                continue;
            }

            files++;
            size += entry.Size;
            compressed += entry.CompressedSize;
            if (entry.IsEncrypted)
            {
                encrypted++;
            }
            else if (!entry.IsSupportedMethod)
            {
                unsupported++;
            }

            if (entry.HasSuspiciousRatio(limits))
            {
                suspicious++;
            }

            if (IsArchiveName(entry.Path))
            {
                nested++;
            }

            int slash = entry.Path.LastIndexOf('/');
            string parent = slash < 0 ? string.Empty : entry.Path[..slash];
            EnsureFolder(tree, folders, parent, null);
            var node = new ArchiveNode(ArchiveEntryName.ForDisplay(entry.FileName), entry.Path, false, entry);
            if (entry.IsReadable && IsImageName(entry.Path))
            {
                node.IsImage = true;
                images.Add(entry);
            }

            tree._children[parent].Add(node);
        }

        foreach ((string path, List<ArchiveNode> children) in tree._children)
        {
            if (folders.TryGetValue(path, out ArchiveNode? folder))
            {
                folder.ChildCount = children.Count;
            }
        }

        images.Sort((a, b) => NaturalStringComparer.Instance.Compare(a.Path, b.Path));
        tree.Images = images;
        tree.Summary = new ArchiveSummary(files, folders.Count, size, compressed, images.Count, encrypted, unsupported, unsafeNames, suspicious, nested);
        return tree;
    }

    /// <summary>Entries directly inside <paramref name="folder"/> ("" for the root): folders first, natural order.</summary>
    public IReadOnlyList<ArchiveNode> Children(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!_children.TryGetValue(folder, out List<ArchiveNode>? children))
        {
            return [];
        }

        if (_sorted.Add(folder))
        {
            children.Sort(static (a, b) => a.IsFolder != b.IsFolder
                ? (a.IsFolder ? -1 : 1)
                : NaturalStringComparer.Instance.Compare(a.Name, b.Name));
        }

        return children;
    }

    /// <summary>True when <paramref name="folder"/> exists in the listing.</summary>
    public bool HasFolder(string folder) => _children.ContainsKey(folder);

    /// <summary>The folder containing <paramref name="path"/> ("" for the root).</summary>
    public static string ParentOf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    private static void EnsureFolder(ArchiveTree tree, Dictionary<string, ArchiveNode> folders, string path, ZipEntry? entry)
    {
        if (path.Length == 0)
        {
            return;
        }

        if (folders.TryGetValue(path, out ArchiveNode? existing))
        {
            return; // declared after its contents, or declared twice
        }

        string parent = ParentOf(path);
        EnsureFolder(tree, folders, parent, null);
        var node = new ArchiveNode(ArchiveEntryName.ForDisplay(path[(path.LastIndexOf('/') + 1)..]), path, true, entry);
        folders[path] = node;
        tree._children[parent].Add(node);
        tree._children.TryAdd(path, []);
    }
}
