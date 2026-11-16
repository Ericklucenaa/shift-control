namespace ShiftControl.Maintenance;

public sealed record FolderSize(string Path, string Name, long Bytes, int Files, bool IsFile, bool Partial);

public sealed record StorageScan(string Root, long TotalBytes, IReadOnlyList<FolderSize> Children, IReadOnlyList<FolderSize> LargestFiles, int Inaccessible);

/// <summary>
/// Mede o que ocupa espaço numa pasta: tamanho de cada subpasta (recursivo) e os maiores arquivos.
/// Não segue junções/atalhos de pasta (evita contar duas vezes) e pula o que não tem permissão de leitura.
/// </summary>
public static class StorageAnalyzer
{
    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    public static StorageScan Scan(string root, IProgress<string>? progress = null, int largestFiles = 25, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        ArgumentOutOfRangeException.ThrowIfNegative(largestFiles);
        var dir = new DirectoryInfo(root);
        var children = new List<FolderSize>();
        var largest = new PriorityQueue<FileInfo, long>();
        int inaccessible = 0;
        long total = 0;

        void TrackLargest(FileInfo f)
        {
            largest.Enqueue(f, f.Length);
            if (largest.Count > largestFiles) largest.Dequeue();
        }

        IEnumerable<DirectoryInfo> subdirs;
        IEnumerable<FileInfo> files;
        try
        {
            subdirs = dir.EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList();
            files = dir.EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return new StorageScan(root, 0, [], [], 1);
        }

        foreach (var f in files)
        {
            children.Add(new FolderSize(f.FullName, f.Name, f.Length, 1, true, false));
            total += f.Length;
            TrackLargest(f);
        }

        foreach (var d in subdirs)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(d.Name);
            long bytes = 0;
            int count = 0;
            bool partial = false;
            try
            {
                foreach (var f in d.EnumerateFiles("*", Recursive))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        bytes += f.Length;
                        count++;
                        TrackLargest(f);
                    }
                    catch (IOException) { partial = true; }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                partial = true;
                inaccessible++;
            }
            children.Add(new FolderSize(d.FullName, d.Name, bytes, count, false, partial));
            total += bytes;
        }

        var big = new List<FolderSize>();
        while (largest.TryDequeue(out var f, out _)) big.Add(new FolderSize(f.FullName, f.Name, f.Length, 1, true, false));
        big.Reverse();
        return new StorageScan(root, total, children.OrderByDescending(c => c.Bytes).ToList(), big, inaccessible);
    }
}
