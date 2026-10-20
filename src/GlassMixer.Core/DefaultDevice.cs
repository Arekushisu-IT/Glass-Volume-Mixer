using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace GlassMixer.Core;

/// <summary>
/// Changes the Windows default output device, like picking one in Sound settings.
///
/// Windows has no public API for this. IPolicyConfig is the private COM interface that
/// the Sound control panel itself uses; it has stayed the same since Windows 7, and
/// apps like EarTrumpet and SoundSwitch rely on it.
/// </summary>
public static class DefaultDevice
{
    public static void Set(string deviceId)
    {
        var config = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
                Marshal.ThrowExceptionForHR(config.SetDefaultEndpoint(deviceId, role));
        }
        finally
        {
            Marshal.ReleaseComObject(config);
        }
    }

    // The COM class Windows creates for us.
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient { }

    // A COM interface is a table of functions in a fixed order. We only call
    // SetDefaultEndpoint, but the ten functions before it must still hold their places.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
        [PreserveSig] int SetEndpointVisibility();
    }
}
