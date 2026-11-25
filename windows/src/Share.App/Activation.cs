using System.Runtime.InteropServices;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;

namespace Share.App;

/// <summary>How Windows started the installed program.</summary>
public sealed record Activation(bool Background, ShareTargetActivatedEventArgs? Share, string? Link)
{
    public static readonly Activation Normal = new(false, null, null);

    public static Activation Current()
    {
        try
        {
            return AppInstance.GetActivatedEventArgs() switch
            {
                ShareTargetActivatedEventArgs s => new Activation(false, s, null),
                ProtocolActivatedEventArgs p => new Activation(false, null, p.Uri.OriginalString),
                { Kind: ActivationKind.StartupTask } => new Activation(true, null, null),
                _ => Normal,
            };
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or InvalidCastException or FileNotFoundException)
        {
            return Normal; // not running as a package
        }
    }
}
