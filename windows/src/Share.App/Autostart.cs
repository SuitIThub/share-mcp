using System.Runtime.InteropServices;
using Windows.ApplicationModel;

namespace Share.App;

/// <summary>The package's startup task; works only for the installed program.</summary>
public sealed class Autostart : IAutostart
{
    public const string TaskId = "ShareAutostart";

    private static async Task<StartupTask?> FindAsync()
    {
        try
        {
            return await StartupTask.GetAsync(TaskId);
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException or FileNotFoundException)
        {
            return null; // not running as a package
        }
    }

    public async Task<bool?> GetAsync() => await FindAsync() is { } t ? t.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy : null;

    public async Task<bool> SetAsync(bool on)
    {
        if (await FindAsync() is not { } t) return false;
        if (!on)
        {
            t.Disable();
            return false;
        }
        return await t.RequestEnableAsync() is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
    }
}
