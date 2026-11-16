using System.Runtime.InteropServices;

namespace ShiftControl.Maintenance;

public enum CleanCategory { UserTemp, WindowsTemp, Thumbnails, RecycleBin, CrashDumps, UpdateDownloads, DirectXShaderCache }

public sealed record CleanTarget(CleanCategory Category, long Bytes, int Files, bool RequiresAdmin, bool Available);

public sealed record CleanResult(CleanCategory Category, long FreedBytes, int Deleted, int Skipped);

/// <summary>
/// Limpeza segura: só pastas temporárias e caches conhecidos que o Windows ou os programas recriam sozinhos.
/// Analisa antes (tamanho e quantidade), apaga só depois da confirmação e só arquivos (nunca segue atalhos de pasta).
/// Arquivos em uso são pulados. Temporários com menos de 24 h são preservados (podem pertencer a instalações em andamento).
/// </summary>
public static class Cleaner
{
    public static readonly TimeSpan TempMinAge = TimeSpan.FromHours(24);

    internal static IReadOnlyList<string> Roots(CleanCategory c)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return c switch
        {
            CleanCategory.UserTemp => [Path.GetTempPath()],
            CleanCategory.WindowsTemp => [Path.Combine(win, "Temp")],
            CleanCategory.Thumbnails => [Path.Combine(local, "Microsoft", "Windows", "Explorer")],
            CleanCategory.CrashDumps => [Path.Combine(local, "CrashDumps"), Path.Combine(win, "Minidump")],
            CleanCategory.UpdateDownloads => [Path.Combine(win, "SoftwareDistribution", "Download")],
            CleanCategory.DirectXShaderCache => [Path.Combine(local, "D3DSCache")],
            _ => [],
        };
    }

    private static bool NeedsAdmin(CleanCategory c) => c is CleanCategory.WindowsTemp or CleanCategory.UpdateDownloads;

    /// <summary>Só arquivos de cache de miniaturas/ícones (thumbcache_*.db, iconcache_*.db) dentro da pasta do Explorer.</summary>
    internal static bool Eligible(CleanCategory c, FileInfo f, DateTime now) => c switch
    {
        CleanCategory.Thumbnails => f.Extension.Equals(".db", StringComparison.OrdinalIgnoreCase) && (f.Name.StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase) || f.Name.StartsWith("iconcache_", StringComparison.OrdinalIgnoreCase)),
        CleanCategory.UserTemp or CleanCategory.WindowsTemp => now - f.LastWriteTime >= TempMinAge,
        CleanCategory.CrashDumps => f.Extension.Equals(".dmp", StringComparison.OrdinalIgnoreCase),
        _ => true,
    };

    public static IReadOnlyList<CleanTarget> Analyze(CancellationToken ct = default)
    {
        var list = new List<CleanTarget>();
        foreach (var c in Enum.GetValues<CleanCategory>())
        {
            ct.ThrowIfCancellationRequested();
            if (c == CleanCategory.RecycleBin)
            {
                var (size, items) = RecycleBinInfo();
                list.Add(new CleanTarget(c, size, (int)Math.Min(items, int.MaxValue), false, true));
                continue;
            }
            bool admin = NeedsAdmin(c);
            long bytes = 0;
            int files = 0;
            foreach (var f in Files(c, ct))
            {
                bytes += f.Length;
                files++;
            }
            list.Add(new CleanTarget(c, bytes, files, admin, !admin || Shell.IsElevated));
        }
        return list;
    }

    private static IEnumerable<FileInfo> Files(CleanCategory c, CancellationToken ct) => Files(c, Roots(c), ct);

    private static IEnumerable<FileInfo> Files(CleanCategory c, IReadOnlyList<string> roots, CancellationToken ct)
    {
        var now = DateTime.Now;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = c != CleanCategory.Thumbnails,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root) || !SafeRoot(root)) continue;
            IEnumerable<FileInfo> files;
            try { files = new DirectoryInfo(root).EnumerateFiles("*", options); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { continue; }
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                if (Eligible(c, f, now)) yield return f;
            }
        }
    }

    /// <summary>Apaga os arquivos das categorias escolhidas. Arquivos em uso ou protegidos são pulados (contados).</summary>
    public static IReadOnlyList<CleanResult> Clean(IEnumerable<CleanCategory> categories, CancellationToken ct = default)
    {
        var results = new List<CleanResult>();
        foreach (var c in categories)
        {
            if (c == CleanCategory.RecycleBin)
            {
                var (size, items) = RecycleBinInfo();
                int hr = SHEmptyRecycleBin(0, null, 0x1 | 0x2 | 0x4); // sem confirmação, sem barra, sem som (o app já confirmou)
                results.Add(new CleanResult(c, hr == 0 ? size : 0, hr == 0 ? (int)items : 0, hr == 0 ? 0 : (int)items));
                continue;
            }
            if (NeedsAdmin(c) && !Shell.IsElevated) continue;
            var cleaned = CleanFiles(c, Roots(c), ct);
            if (c is CleanCategory.UserTemp or CleanCategory.WindowsTemp or CleanCategory.UpdateDownloads)
                RemoveEmptyDirectories(Roots(c));
            results.Add(cleaned);
        }
        return results;
    }

    internal static CleanResult CleanFiles(CleanCategory c, IReadOnlyList<string> roots, CancellationToken ct = default)
    {
        long freed = 0;
        int deleted = 0, skipped = 0;
        var finalRoots = roots.Select(r => FinalPath(r, directory: true)).OfType<string>().ToList();
        foreach (var f in Files(c, roots, ct).ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (!SafeFile(f.FullName, roots)) { skipped++; continue; }
            try
            {
                f.Refresh();
                if (!f.Exists || !Eligible(c, f, DateTime.Now)) { skipped++; continue; }
                long length = f.Length;
                if (!DeleteWithinRoots(f.FullName, finalRoots)) { skipped++; continue; }
                freed += length;
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return new(c, freed, deleted, skipped);
    }

    internal static bool IsUnderRoot(string path, IReadOnlyList<string> roots)
    {
        string full = Path.GetFullPath(path);
        return roots.Any(r => full.StartsWith(Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool SafeRoot(string root)
    {
        string full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (full.Equals(Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return false;
        if (full.Equals(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase)) return false;
        if (full.Equals(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)) return false;
        for (var dir = new DirectoryInfo(full); dir is not null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    internal static bool SafeFile(string path, IReadOnlyList<string> roots)
    {
        if (!IsUnderRoot(path, roots)) return false;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            return SafeRoot(Path.GetDirectoryName(path)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Exclui pelo handle, depois de conferir o caminho real do próprio handle. Com o app elevado, um processo sem
    /// privilégio poderia trocar uma subpasta do Temp por uma junção entre a checagem e a exclusão por nome; aqui o
    /// arquivo verificado é exatamente o excluído. Links são abertos como links (nunca o destino).
    /// </summary>
    internal static bool DeleteWithinRoots(string path, IReadOnlyList<string> finalRoots)
    {
        using var handle = CreateFileW(path, Delete | FileReadAttributes, ShareAll, 0, OpenExisting, FlagOpenReparsePoint, 0);
        if (handle.IsInvalid) return false;
        string? final = FinalPath(handle);
        if (final is null || !finalRoots.Any(r => final.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) return false;
        byte deleteFile = 1;
        return SetFileInformationByHandle(handle, FileDispositionInfo, ref deleteFile, 1);
    }

    /// <summary>Caminho real (sem 8.3, junções ou links resolvidos pelo nome) de um arquivo ou pasta.</summary>
    internal static string? FinalPath(string path, bool directory = false)
    {
        using var handle = CreateFileW(path, FileReadAttributes, ShareAll, 0, OpenExisting, directory ? FlagBackupSemantics : FlagOpenReparsePoint, 0);
        return handle.IsInvalid ? null : FinalPath(handle);
    }

    private static string? FinalPath(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        var buffer = new char[1024];
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        return length is 0 || length >= buffer.Length ? null : new string(buffer, 0, (int)length);
    }

    private const uint Delete = 0x00010000, FileReadAttributes = 0x80, ShareAll = 0x7, OpenExisting = 3;
    private const uint FlagOpenReparsePoint = 0x00200000, FlagBackupSemantics = 0x02000000;
    private const int FileDispositionInfo = 4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle handle, [Out] char[] buffer, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int infoClass, ref byte info, uint size);

    private static void RemoveEmptyDirectories(IReadOnlyList<string> roots)
    {
        foreach (var root in roots.Where(r => Directory.Exists(r) && SafeRoot(r)))
        {
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).OrderByDescending(d => d.Length).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var d in dirs)
            {
                try { if (SafeRoot(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    #region Lixeira

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ShQueryRbInfo { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? root, ref ShQueryRbInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(nint hwnd, string? root, uint flags);

    public static (long Size, long Items) RecycleBinInfo()
    {
        var info = new ShQueryRbInfo { cbSize = Marshal.SizeOf<ShQueryRbInfo>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }

    #endregion
}
