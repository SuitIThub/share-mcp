namespace Share.Core.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("share-test").FullName;

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string File(string name, byte[] content)
    {
        System.IO.File.WriteAllBytes(File(name), content);
        return File(name);
    }

    public static byte[] Random(int size, int seed = 1)
    {
        var b = new byte[size];
        new Random(seed).NextBytes(b);
        return b;
    }

    public void Dispose()
    {
        for (var i = 0; ; i++)
        {
            try
            {
                Directory.Delete(Path, true);
                return;
            }
            catch (Exception) when (i < 20)
            {
                Thread.Sleep(50);
            }
            catch (IOException)
            {
                return;
            }
        }
    }
}

/// <summary>Counts and records what happens to a source.</summary>
public sealed class Probe(IFileSource inner) : IFileSource
{
    private int _released;
    public readonly List<long> Opened = [];
    public int Released => _released;
    public Func<long, Stream>? Open { get; set; }
    public FileItem Item => inner.Item;
    public bool Unchanged() => inner.Unchanged();

    public Stream OpenAt(long offset)
    {
        lock (Opened) Opened.Add(offset);
        return Open is null ? inner.OpenAt(offset) : Open(offset);
    }

    public void Release()
    {
        Interlocked.Increment(ref _released);
        inner.Release();
    }
}

/// <summary>Serves bytes slowly so tests can interrupt a transfer in the middle.</summary>
public sealed class SlowSource(IFileSource inner, int chunk, int delayMs) : IFileSource
{
    public FileItem Item => inner.Item;
    public bool Unchanged() => inner.Unchanged();
    public void Release() => inner.Release();
    public Stream OpenAt(long offset) => new Slow(inner.OpenAt(offset), chunk, delayMs);

    private sealed class Slow(Stream s, int chunk, int delayMs) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(delayMs, ct);
            return await s.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], ct);
        }

        protected override void Dispose(bool disposing) => s.Dispose();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public static class Eventually
{
    public static async Task True(Func<bool> cond, string what, int seconds = 15)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!cond())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException(what);
            await Task.Delay(10);
        }
    }

    public static async Task True(Func<Task<bool>> cond, string what, int seconds = 15)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!await cond())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException(what);
            await Task.Delay(10);
        }
    }
}
