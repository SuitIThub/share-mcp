using System.Runtime.InteropServices;

namespace Share.App;

internal static partial class Native
{
    [Flags]
    internal enum ExecutionState : uint
    {
        Continuous = 0x80000000,
        SystemRequired = 0x00000001,
    }

    [LibraryImport("kernel32.dll")]
    internal static partial ExecutionState SetThreadExecutionState(ExecutionState flags);

    /// <summary>Lets the running instance come to the front when a second start asks it to.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AllowSetForegroundWindow(int processId);

    internal const int AnyProcess = -1;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint icon);
}
