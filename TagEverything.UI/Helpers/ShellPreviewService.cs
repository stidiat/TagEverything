using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace TagEverything.UI.Helpers;

/// <summary>Retrieves Explorer previews off the UI thread. These images are display-only.</summary>
public sealed class ShellPreviewService
{
    public void ClearCache() => _cache.Clear();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<byte[]?> GetPreviewAsync(string path, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string key;
        try
        {
            var info = new FileInfo(path);
            key = info.Exists ? $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"
                : "type:" + info.Extension;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }

        if (_cache.TryGetValue(key, out var cached)) return cached;
        await _gate.WaitAsync(token);
        try
        {
            if (_cache.TryGetValue(key, out cached)) return cached;
            token.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = new Thread(() =>
            {
                try { completion.TrySetResult(Extract(path)); }
                catch (Exception) { completion.TrySetResult(null); }
            }) { IsBackground = true, Name = "Shell preview" };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            var bytes = await completion.Task;
            token.ThrowIfCancellationRequested();
            if (bytes != null)
            {
                if (_cache.Count >= 256) _cache.Clear();
                _cache[key] = bytes;
            }
            return bytes;
        }
        finally { _gate.Release(); }
    }

    private static byte[]? Extract(string path)
    {
        if (File.Exists(path))
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            IShellItemImageFactory? factory = null;
            try
            {
                if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) >= 0 && factory != null)
                {
                    // Default flags: thumbnail if supported, otherwise the item's icon.
                    foreach (var flags in new uint[] { 0, 4 })
                    {
                        IntPtr bitmap = IntPtr.Zero;
                        try
                        {
                            if (factory.GetImage(new NativeSize { Width = 256, Height = 256 }, flags, out bitmap) >= 0 && bitmap != IntPtr.Zero)
                                return Encode(Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()));
                        }
                        finally { if (bitmap != IntPtr.Zero) DeleteObject(bitmap); }
                    }
                }
            }
            catch (COMException) { /* Use the associated type icon below. */ }
            finally { if (factory != null) Marshal.ReleaseComObject(factory); }
        }

        // USEFILEATTRIBUTES also works if the physical file is no longer present.
        var result = SHGetFileInfo(path, 0x80, out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100 | 0x10);
        try
        {
            return result != IntPtr.Zero && info.Icon != IntPtr.Zero
                ? Encode(Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()))
                : null;
        }
        finally { if (info.Icon != IntPtr.Zero) DestroyIcon(info.Icon); }
    }

    private static byte[] Encode(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize { public int Width; public int Height; }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, uint flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
}
