using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TagEverything.Core.Services;

internal sealed class UnsafeLibraryPathException(string path) : IOException($"库路径包含重解析点：{path}");

internal static class LibraryPaths
{
    public const string DataDirectory = "TagEverythingData";
    public static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string Resolve(string root, string relativePath, bool contentOnly = false)
    {
        if (Path.IsPathRooted(relativePath)) throw new ArgumentException("Library paths must be relative.", nameof(relativePath));
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (path.Length <= prefix.Length || !path.StartsWith(prefix, Comparison))
            throw new InvalidOperationException("The file path is outside the configured library.");

        var segments = path[prefix.Length..].Split(Path.DirectorySeparatorChar);
        if ((contentOnly && segments[0].Equals(DataDirectory, StringComparison.OrdinalIgnoreCase)) ||
            (OperatingSystem.IsWindows() && segments.Any(part => part.Contains(':') || part.EndsWith('.') || part.EndsWith(' '))))
            throw new IOException("库文件路径无效。");

        RejectReparsePoint(root);
        var current = root;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            RejectReparsePoint(current);
        }
        return path;
    }

    public static string Internal(string root, params string[] parts) => Resolve(root, Path.Combine([DataDirectory, .. parts]));

    public static bool IsInside(string relativePath) => !Path.IsPathRooted(relativePath) &&
        relativePath != ".." && !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, Comparison);

    public static IDisposable HoldDirectory(string root, string directory, bool create = false)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!directory.Equals(root, Comparison)) Resolve(root, Path.GetRelativePath(root, directory));
        var lease = new DirectoryLease();
        try
        {
            lease.Hold(root);
            if (!directory.Equals(root, Comparison))
            {
                var current = root;
                foreach (var segment in Path.GetRelativePath(root, directory).Split(Path.DirectorySeparatorChar))
                {
                    current = Path.Combine(current, segment);
                    if (create) Directory.CreateDirectory(current);
                    lease.Hold(current);
                }
            }
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static void RejectReparsePoint(string path)
    {
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new UnsafeLibraryPathException(path);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private sealed class DirectoryLease : IDisposable
    {
        private readonly Stack<SafeFileHandle> _handles = new();

        public void Hold(string directory)
        {
            if (!OperatingSystem.IsWindows())
            {
                RejectReparsePoint(directory);
                return;
            }

            // Keep each ancestor open without delete sharing while a file is moved.
            var handle = WindowsFiles.OpenDirectory(directory);
            _handles.Push(handle);
            var attributes = File.GetAttributes(handle);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new UnsafeLibraryPathException(directory);
            if (!attributes.HasFlag(FileAttributes.Directory)) throw new IOException($"库路径不是目录：{directory}");
        }

        public void Dispose()
        {
            while (_handles.TryPop(out var handle)) handle.Dispose();
        }
    }
}

internal static class WindowsFiles
{
    private const uint BackupSemantics = 0x02000000;
    private const uint ListDirectory = 1;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint GenericRead = 0x80000000;
    private const uint DeleteAccess = 0x00010000;
    private const int FileRenameInfo = 3;
    private const int FileDispositionInfo = 4;
    private const int FileIdInfo = 18;

    public static SafeFileHandle OpenDirectory(string path) => Open(path, ListDirectory, FileShare.ReadWrite, BackupSemantics);

    private static SafeFileHandle Open(string path, uint access, FileShare share, uint flags = 0)
    {
        var handle = CreateFile(path, access, share, IntPtr.Zero, FileMode.Open, flags | OpenReparsePoint, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new IOException($"无法安全打开文件或目录：{path}", new Win32Exception(error));
    }

    public static FileStream OpenForMove(string path)
        => OpenRead(path, DeleteAccess);

    public static FileStream OpenRead(string path) => OpenRead(path, 0);

    private static FileStream OpenRead(string path, uint additionalAccess)
    {
        if (!OperatingSystem.IsWindows()) return File.OpenRead(path);
        var handle = Open(path, GenericRead | additionalAccess, FileShare.Read);
        try
        {
            if (File.GetAttributes(handle).HasFlag(FileAttributes.ReparsePoint)) throw new UnsafeLibraryPathException(path);
            return new FileStream(handle, FileAccess.Read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static string? Identity(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (GetFileInformationByHandleEx(handle, FileIdInfo, out var info, Marshal.SizeOf<FileId>()))
            return $"{info.Volume:X16}:{info.High:X16}{info.Low:X16}";

        var error = Marshal.GetLastWin32Error();
        // Some network providers and older filesystems do not implement FILE_ID_INFO.
        if (error is 1 or 50 or 87 or 120) return null;
        throw new IOException("无法读取文件标识。", new Win32Exception(error));
    }

    public static void Move(FileStream source, string sourcePath, string destination)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Move(sourcePath, destination);
            return;
        }

        // FILE_RENAME_INFO consists of flags, an aligned HANDLE, then a length and UTF-16 name.
        var name = System.Text.Encoding.Unicode.GetBytes(destination);
        var rootOffset = IntPtr.Size;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(int);
        var size = nameOffset + name.Length + sizeof(char);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.WriteInt32(buffer, 0);
            Marshal.WriteIntPtr(buffer, rootOffset, IntPtr.Zero);
            Marshal.WriteInt32(buffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, buffer + nameOffset, name.Length);
            Marshal.WriteInt16(buffer, nameOffset + name.Length, 0);
            if (!SetFileInformationByHandle(source.SafeFileHandle, FileRenameInfo, buffer, size))
                throw new IOException($"无法安全移动文件：{sourcePath}", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static void Delete(FileStream file, string path)
    {
        if (OperatingSystem.IsWindows())
        {
            byte delete = 1;
            if (!SetFileInformationByHandle(file.SafeFileHandle, FileDispositionInfo, ref delete, sizeof(byte)))
                throw new IOException($"无法安全删除文件：{path}", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        else File.Delete(path);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId
    {
        public ulong Volume;
        public ulong Low;
        public ulong High;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
        FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileId info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, IntPtr info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref byte info, int size);
}
