using System.Runtime.InteropServices;
using System.Text.Json;

namespace Share.Core;

/// <summary>
/// Downloads into a folder. Incomplete files are called name.tid-n.part; which part belongs to which offer, and what is
/// already finished, is remembered in a small state file.
/// </summary>
public sealed partial class DiskDownloads(string directory, string stateFile) : IDownloadStore
{
    /// <summary>guards all state files; there is one per process in practice</summary>
    private static readonly object Lock = new();

    private sealed record Entry(string? Part = null, string? Done = null);

    /// <summary>The user's Downloads folder, wherever it was moved to.</summary>
    public static string DefaultDirectory
    {
        get
        {
            if (OperatingSystem.IsWindows() && SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, 0, out var p) == 0)
            {
                try
                {
                    return Marshal.PtrToStringUni(p)!;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(p);
                }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);

    public IPart Part(string transferId, int index, FileItem item) => new DiskPart(this, $"{transferId}-{index}", Names.SafeFileName(item.Name),
        Path.Combine(directory, $"{Names.SafeFileName(item.Name)}.{transferId[..Math.Min(8, transferId.Length)]}-{index}.part"));

    public void Forget(string transferId) => Drop(id => id == transferId);

    public void Retain(IReadOnlySet<string> transferIds) => Drop(id => !transferIds.Contains(id));

    private void Drop(Func<string, bool> which)
    {
        lock (Lock)
        {
            var state = Load();
            var gone = state.Where(e => which(e.Key[..e.Key.LastIndexOf('-')])).ToList();
            if (gone.Count == 0) return;
            foreach (var (key, entry) in gone)
            {
                if (entry.Part is not null) TryDelete(entry.Part);
                state.Remove(key);
            }
            Save(state);
        }
    }

    private Dictionary<string, Entry> Load()
    {
        try
        {
            var all = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllBytes(stateFile)) ?? [];
            return all.Where(e => e.Value is not null && e.Key.Contains('-')).ToDictionary(e => e.Key, e => e.Value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private void Save(Dictionary<string, Entry> state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        var tmp = stateFile + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(state));
        File.Move(tmp, stateFile, true);
    }

    private Entry? Get(string key)
    {
        lock (Lock) return Load().GetValueOrDefault(key);
    }

    private void Set(string key, Entry entry)
    {
        lock (Lock)
        {
            var state = Load();
            if (state.GetValueOrDefault(key) == entry) return;
            state[key] = entry;
            Save(state);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // in use; a left-over .part does no harm
        }
    }

    private sealed class DiskPart(DiskDownloads store, string key, string name, string part) : IPart
    {
        public string? FinishedRef => store.Get(key)?.Done is { } done && File.Exists(done) ? done : null;

        public long Size()
        {
            var f = new FileInfo(part);
            return f.Exists ? f.Length : 0;
        }

        public Stream Append(long offset) => Io(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(part)!);
            if (Size() < offset) throw new StorageBusyException($"{name} is shorter than expected");
            store.Set(key, new Entry(Part: part));
            var s = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 1, FileOptions.Asynchronous);
            try
            {
                s.SetLength(offset);
                s.Position = offset;
                return s;
            }
            catch
            {
                s.Dispose();
                throw;
            }
        });

        public void Reset() => Io(() =>
        {
            File.Delete(part);
            return 0;
        });

        public string Finish() => Io(() =>
        {
            var dir = Path.GetDirectoryName(part)!;
            Directory.CreateDirectory(dir);
            if (!File.Exists(part)) File.Create(part).Dispose();
            while (true)
            {
                var target = Path.Combine(dir, Names.UniqueName(name, n => Path.Exists(Path.Combine(dir, n))));
                try
                {
                    File.Move(part, target, false); // never replaces: whoever comes second gets the next name
                }
                catch (IOException) when (Path.Exists(target))
                {
                    continue;
                }
                catch (UnauthorizedAccessException e)
                {
                    throw new StorageBusyException($"cannot finish {name}", e); // e.g. a virus scanner looking at it
                }
                store.Set(key, new Entry(Done: target));
                return target;
            }
        });

        private static T Io<T>(Func<T> f)
        {
            try
            {
                return f();
            }
            catch (UnauthorizedAccessException e)
            {
                throw new IOException(e.Message, e);
            }
            catch (IOException e) when ((e.HResult & 0xFFFF) is 32 or 33) // sharing or lock violation
            {
                throw new StorageBusyException(e.Message, e);
            }
        }
    }
}
