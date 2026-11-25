using System.Diagnostics;
using Share.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.Storage;

namespace Share.App;

/// <summary>An item from the share menu; some have no path, only a stream.</summary>
public sealed record SharedItem(string Name, string? Path, Func<Task<Stream>> OpenAsync);

/// <summary>What the Windows share menu hands over.</summary>
public interface IShareData
{
    Task<IReadOnlyList<SharedItem>> FilesAsync();
    Task<string?> LinkAsync();
    Task<string?> TextAsync();
    Task<Stream?> ImageAsync();
    void Started();
    void DataRetrieved();
    void Completed();
    void Error(string message);
}

public static class ShareTarget
{
    /// <summary>Reads the shared content and hands it to the running program; only then may the sharing program clean up.</summary>
    /// <param name="send">true once the content has been taken over (files are open)</param>
    public static async Task HandleAsync(IShareData d, string tempDir, Func<Command, Task<bool>> send)
    {
        Command? c;
        try
        {
            d.Started();
            c = await ReadAsync(d, tempDir).ConfigureAwait(false);
            d.DataRetrieved();
        }
        catch (Exception e) // other programs fail in all possible ways
        {
            Trace.TraceWarning("share failed: " + e);
            d.Error("Der Inhalt konnte nicht gelesen werden");
            return;
        }
        if (c is null) d.Error("Hier gibt es nichts, was gesendet werden kann");
        else if (await send(c).ConfigureAwait(false)) d.Completed();
        else d.Error("Share reagiert nicht");
    }

    private static async Task<Command?> ReadAsync(IShareData d, string tempDir)
    {
        var items = await d.FilesAsync().ConfigureAwait(false);
        if (items.Count > 0)
        {
            var paths = new List<string>();
            foreach (var i in items) paths.Add(string.IsNullOrEmpty(i.Path) ? await CopyAsync(i, tempDir).ConfigureAwait(false) : i.Path);
            return new Command.Send(paths);
        }
        if (await d.LinkAsync().ConfigureAwait(false) is { Length: > 0 } link) return new Command.SendText(link);
        if (await d.TextAsync().ConfigureAwait(false) is { } text && !string.IsNullOrWhiteSpace(text)) return new Command.SendText(text);
        if (await d.ImageAsync().ConfigureAwait(false) is not { } image) return null;
        var picture = new SharedItem($"Bild {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png", null, () => Task.FromResult(image));
        return new Command.Send([await CopyAsync(picture, tempDir).ConfigureAwait(false)]);
    }

    /// <summary>Into a folder of its own, so the name can stay as it is.</summary>
    private static async Task<string> CopyAsync(SharedItem i, string tempDir)
    {
        var dir = Path.Combine(tempDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Names.SafeFileName(i.Name));
        await using var input = await i.OpenAsync().ConfigureAwait(false);
        await using var output = File.Create(path);
        await input.CopyToAsync(output).ConfigureAwait(false);
        return path;
    }
}

public sealed class WindowsShareData(ShareOperation op) : IShareData
{
    public async Task<IReadOnlyList<SharedItem>> FilesAsync()
    {
        if (!op.Data.Contains(StandardDataFormats.StorageItems)) return [];
        var items = await op.Data.GetStorageItemsAsync();
        // a folder keeps its path and is refused with a message later
        return [.. items.Select(i => new SharedItem(i.Name, i.Path, async () => i is StorageFile f ? await f.OpenStreamForReadAsync() : throw new IOException("not a file")))];
    }

    public async Task<string?> LinkAsync() => op.Data.Contains(StandardDataFormats.WebLink) ? (await op.Data.GetWebLinkAsync())?.AbsoluteUri : null;

    public async Task<string?> TextAsync() => op.Data.Contains(StandardDataFormats.Text) ? await op.Data.GetTextAsync() : null;

    /// <summary>Always as PNG: the share menu delivers whatever the other program had at hand.</summary>
    public async Task<Stream?> ImageAsync()
    {
        if (!op.Data.Contains(StandardDataFormats.Bitmap)) return null;
        using var raw = await (await op.Data.GetBitmapAsync()).OpenReadAsync();
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(raw);
        using var bitmap = await decoder.GetSoftwareBitmapAsync();
        var png = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, png);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        png.Seek(0);
        return png.AsStreamForRead();
    }

    public void Started() => op.ReportStarted();
    public void DataRetrieved() => op.ReportDataRetrieved();
    public void Completed() => op.ReportCompleted();
    public void Error(string message) => op.ReportError(message);
}
