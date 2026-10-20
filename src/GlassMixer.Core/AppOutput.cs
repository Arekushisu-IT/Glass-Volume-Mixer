using System.Runtime.InteropServices;

namespace GlassMixer.Core;

/// <summary>
/// Sends one app's sound to a chosen output device, like
/// Settings > System > Sound > Volume mixer > [app] > Output device.
///
/// Windows does this with a private Windows Runtime class, AudioPolicyConfig.
/// It has no public documentation; this layout is the one EarTrumpet uses on
/// Windows 10 21H2 and later, including Windows 11. If Windows ever changes it,
/// IsSupported turns false and the mixer simply disables the Output menu.
///
/// Many apps only switch device when they next start a sound (pause and play again).
/// </summary>
static class AppOutput
{
    // Windows stores the choice as a device-interface path built from the device id.
    const string Prefix = @"\\?\SWD#MMDEVAPI#";
    const string RenderSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    const int Render = 0, RoleConsole = 0, RoleMultimedia = 1;

    static IAudioPolicyConfigFactory? _factory;
    static bool _created;

    public static bool IsSupported => Factory is not null;

    /// <summary>The device id this process is pinned to, or null when it follows the default.</summary>
    public static string? Get(int processId)
    {
        if (Factory is null || processId <= 0) return null;
        if (Factory.GetPersistedDefaultAudioEndpoint((uint)processId, Render, RoleMultimedia, out IntPtr hstring) < 0) return null;
        try
        {
            var path = ReadHString(hstring);
            if (string.IsNullOrEmpty(path)) return null;
            if (path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) path = path[Prefix.Length..];
            int end = path.IndexOf('#');
            return end > 0 ? path[..end] : path;
        }
        finally
        {
            if (hstring != IntPtr.Zero) WindowsDeleteString(hstring);
        }
    }

    /// <summary>Pins a process to a device, or with null returns it to the Windows default.</summary>
    public static void Set(int processId, string? deviceId)
    {
        if (Factory is null || processId <= 0) return;
        IntPtr hstring = IntPtr.Zero;
        try
        {
            if (deviceId is not null)
            {
                var path = Prefix + deviceId + RenderSuffix;
                Marshal.ThrowExceptionForHR(WindowsCreateString(path, path.Length, out hstring));
            }
            foreach (var role in new[] { RoleConsole, RoleMultimedia })
                Marshal.ThrowExceptionForHR(Factory.SetPersistedDefaultAudioEndpoint((uint)processId, Render, role, hstring));
        }
        finally
        {
            if (hstring != IntPtr.Zero) WindowsDeleteString(hstring);
        }
    }

    static IAudioPolicyConfigFactory? Factory
    {
        get
        {
            if (_created) return _factory;
            _created = true;
            const string className = "Windows.Media.Internal.AudioPolicyConfig";
            IntPtr name = IntPtr.Zero;
            try
            {
                if (WindowsCreateString(className, className.Length, out name) < 0) return null;
                var iid = typeof(IAudioPolicyConfigFactory).GUID;
                if (RoGetActivationFactory(name, ref iid, out IntPtr pointer) < 0) return null;
                try { _factory = (IAudioPolicyConfigFactory)Marshal.GetObjectForIUnknown(pointer); }
                finally { Marshal.Release(pointer); }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or EntryPointNotFoundException)
            {
                _factory = null;
            }
            finally
            {
                if (name != IntPtr.Zero) WindowsDeleteString(name);
            }
            return _factory;
        }
    }

    // HSTRING is the Windows Runtime string type: created and freed with these functions.
    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    [DllImport("combase.dll")]
    static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    static string? ReadHString(IntPtr hstring)
    {
        if (hstring == IntPtr.Zero) return null;
        var buffer = WindowsGetStringRawBuffer(hstring, out uint length);
        return Marshal.PtrToStringUni(buffer, (int)length);
    }

    // A Windows Runtime interface starts with IInspectable's three methods, then its own.
    // Only the last three are used; the rest hold their places in the function table.
    [ComImport, Guid("ab3d4648-e242-459f-b02f-541c70306324"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioPolicyConfigFactory
    {
        [PreserveSig] int GetIids();
        [PreserveSig] int GetRuntimeClassName();
        [PreserveSig] int GetTrustLevel();

        [PreserveSig] int add_CtxVolumeChange();
        [PreserveSig] int remove_CtxVolumeChanged();
        [PreserveSig] int add_RingerVibrateStateChanged();
        [PreserveSig] int remove_RingerVibrateStateChange();
        [PreserveSig] int SetVolumeGroupGainForId();
        [PreserveSig] int GetVolumeGroupGainForId();
        [PreserveSig] int GetActiveVolumeGroupForEndpointId();
        [PreserveSig] int GetVolumeGroupsForEndpoint();
        [PreserveSig] int GetCurrentVolumeContext();
        [PreserveSig] int SetVolumeGroupMuteForId();
        [PreserveSig] int GetVolumeGroupMuteForId();
        [PreserveSig] int SetRingerVibrateState();
        [PreserveSig] int GetRingerVibrateState();
        [PreserveSig] int SetPreferredChatApplication();
        [PreserveSig] int ResetPreferredChatApplication();
        [PreserveSig] int GetPreferredChatApplication();
        [PreserveSig] int GetCurrentChatApplications();
        [PreserveSig] int add_ChatContextChanged();
        [PreserveSig] int remove_ChatContextChanged();

        [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, IntPtr deviceId);
        [PreserveSig] int GetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, out IntPtr deviceId);
        [PreserveSig] int ClearAllPersistedApplicationDefaultEndpoints();
    }
}
