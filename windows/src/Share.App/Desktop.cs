using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace Share.App;

/// <summary>Clipboard and energy saving; both want the UI thread.</summary>
public sealed class Desktop(Dispatcher dispatcher) : IDesktop
{
    public void CopyText(string text) => dispatcher.InvokeAsync(() => RetryAsync(() => Clipboard.SetDataObject(text, true), TimeSpan.FromMilliseconds(50)));

    /// <summary>Another program may have the clipboard open at this very moment.</summary>
    internal static async Task<bool> RetryAsync(Action copy, TimeSpan pause)
    {
        for (var i = 0; i < 20; i++)
        {
            try
            {
                copy();
                return true;
            }
            catch (ExternalException)
            {
                await Task.Delay(pause);
            }
        }
        Trace.TraceWarning("clipboard stays busy");
        return false;
    }

    // per thread, so always the same one
    public void KeepAwake(bool on) => dispatcher.InvokeAsync(() =>
        Native.SetThreadExecutionState(on ? Native.ExecutionState.Continuous | Native.ExecutionState.SystemRequired : Native.ExecutionState.Continuous));
}
