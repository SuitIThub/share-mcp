namespace Share.Core;

/// <summary>Something that can be offered: readable from any offset as long as the offer lasts.</summary>
public interface IFileSource
{
    FileItem Item { get; }

    /// <summary>False if the content no longer matches what was offered.</summary>
    bool Unchanged();

    Stream OpenAt(long offset);

    /// <summary>Called exactly once when the offer has ended.</summary>
    void Release();
}

/// <summary>An open offer of this device.</summary>
public sealed record Offer(Transfer Transfer, byte[] Secret, IReadOnlyList<IFileSource> Sources);

/// <summary>The item cannot be read; the message is shown to the user.</summary>
public sealed class SourceException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>The storage cannot be used right now; worth another try, unlike other storage errors.</summary>
public sealed class StorageBusyException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>One file being downloaded. Its state survives a restart of the process.</summary>
public interface IPart
{
    /// <summary>Reference to the finished file, or null while it is incomplete.</summary>
    string? FinishedRef { get; }

    /// <summary>Bytes stored so far; 0 only if there really is nothing.</summary>
    long Size();

    /// <summary>Continues at exactly this offset: anything stored behind it is cut off. StorageBusyException if less is stored.</summary>
    Stream Append(long offset);

    /// <summary>Drops the stored bytes.</summary>
    void Reset();

    /// <summary>Moves the complete file to its final place and returns a reference to it.</summary>
    string Finish();
}

/// <summary>Where downloads go.</summary>
public interface IDownloadStore
{
    IPart Part(string transferId, int index, FileItem item);

    /// <summary>Discards incomplete parts and all state of this offer.</summary>
    void Forget(string transferId);

    /// <summary>Forgets every offer except these.</summary>
    void Retain(IReadOnlySet<string> transferIds);
}
