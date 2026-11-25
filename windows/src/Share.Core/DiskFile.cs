using Microsoft.Win32.SafeHandles;

namespace Share.Core;

/// <summary>
/// A file on disk. Its handle stays open for the whole offer without blocking other programs: they may write, rename or
/// delete the file. A file replaced by a new one keeps being served as it was offered; one changed in place counts as changed.
/// </summary>
public sealed class DiskFile : IFileSource, IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly string? _delete;
    private int _released;

    private DiskFile(SafeFileHandle handle, FileItem item, string? delete)
    {
        _handle = handle;
        Item = item;
        _delete = delete;
    }

    public FileItem Item { get; }

    /// <summary>Opens the file; throws SourceException with a message for the user.</summary>
    public static DiskFile Open(string path, bool deleteOnRelease = false)
    {
        var name = path;
        try
        {
            name = Path.GetFileName(path) is { Length: > 0 } n ? n : path;
            if (Directory.Exists(path)) throw new SourceException($"„{name}“ ist ein Ordner. Ordner können nicht gesendet werden.");
            var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var item = new FileItem(name, RandomAccess.GetLength(h)) { Mime = MimeOf(name), ModTime = File.GetLastWriteTimeUtc(h) };
            return new DiskFile(h, item, deleteOnRelease ? path : null);
        }
        catch (Exception e) when (e is not SourceException and (IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException))
        {
            throw new SourceException($"„{name}“ kann nicht gelesen werden", e);
        }
    }

    public bool Unchanged()
    {
        try
        {
            return RandomAccess.GetLength(_handle) == Item.Size && File.GetLastWriteTimeUtc(_handle) == Item.ModTime.UtcDateTime;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public Stream OpenAt(long offset) => new Reader(_handle, offset);

    public void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        _handle.Dispose();
        if (_delete is null) return;
        try
        {
            File.Delete(_delete);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a temporary copy; the system cleans up its folder
        }
    }

    public void Dispose() => Release();

    private static string MimeOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".apk" => "application/vnd.android.package-archive",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".mp4" => "video/mp4",
        ".mkv" => "video/x-matroska",
        ".mp3" => "audio/mpeg",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        ".txt" => "text/plain",
        ".html" or ".htm" => "text/html",
        ".json" => "application/json",
        _ => "application/octet-stream",
    };

    /// <summary>Positional reads, so any number of readers can share the handle.</summary>
    private sealed class Reader(SafeFileHandle handle, long position) : Stream
    {
        private long _position = position;

        public override int Read(Span<byte> buffer)
        {
            try
            {
                var n = RandomAccess.Read(handle, buffer, _position);
                _position += n;
                return n;
            }
            catch (ObjectDisposedException e)
            {
                throw new IOException("released", e);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            try
            {
                var n = await RandomAccess.ReadAsync(handle, buffer, _position, ct).ConfigureAwait(false);
                _position += n;
                return n;
            }
            catch (ObjectDisposedException e)
            {
                throw new IOException("released", e);
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
