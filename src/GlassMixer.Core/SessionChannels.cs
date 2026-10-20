using System.Runtime.InteropServices;

namespace GlassMixer.Core;

/// <summary>
/// Left/right balance for an app.
///
/// Every audio session has an IChannelAudioVolume: one level per speaker channel
/// (0 = left, 1 = right). It's a documented Core Audio interface; NAudio just doesn't
/// wrap it, so we declare the COM interfaces ourselves and walk the sessions directly.
/// Balance -1 means left only, 0 centre, +1 right only.
/// </summary>
static class SessionChannels
{
    const int CLSCTX_ALL = 23;

    public static void SetBalance(IEnumerable<string> deviceIds, IReadOnlySet<int> pids, float balance)
    {
        balance = Math.Clamp(balance, -1f, 1f);
        float left = balance > 0 ? 1 - balance : 1;
        float right = balance < 0 ? 1 + balance : 1;
        ForEachSession(deviceIds, pids, channels =>
        {
            if (channels.GetChannelCount(out uint count) < 0 || count < 2) return;
            var context = Guid.Empty;
            channels.SetChannelVolume(0, left, ref context);
            channels.SetChannelVolume(1, right, ref context);
        });
    }

    /// <summary>Reads the balance of the first stereo session found, or 0.</summary>
    public static float GetBalance(IEnumerable<string> deviceIds, IReadOnlySet<int> pids)
    {
        float? found = null;
        ForEachSession(deviceIds, pids, channels =>
        {
            if (found is not null) return;
            if (channels.GetChannelCount(out uint count) < 0 || count < 2) return;
            if (channels.GetChannelVolume(0, out float l) < 0 || channels.GetChannelVolume(1, out float r) < 0) return;
            if (l <= 0 && r <= 0) { found = 0; return; }
            found = l >= r ? r / l - 1 : 1 - l / r; // the reverse of SetBalance
        });
        return found ?? 0;
    }

    static void ForEachSession(IEnumerable<string> deviceIds, IReadOnlySet<int> pids, Action<IChannelAudioVolume> action)
    {
        if (pids.Count == 0) return;
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorClass();
        try
        {
            foreach (var deviceId in deviceIds)
            {
                if (enumerator.GetDevice(deviceId, out var device) < 0) continue;
                var iid = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object managerObject) < 0) continue;
                var manager = (IAudioSessionManager2)managerObject;
                if (manager.GetSessionEnumerator(out var sessions) < 0) continue;
                if (sessions.GetCount(out int count) < 0) continue;

                for (int i = 0; i < count; i++)
                {
                    if (sessions.GetSession(i, out var session) < 0) continue;
                    // GetProcessId can return a success code above 0 for multi-process sessions.
                    if (session.GetProcessId(out uint pid) < 0 || !pids.Contains((int)pid)) continue;
                    if (session is IChannelAudioVolume channels) action(channels); // "is" asks COM for the interface
                }
            }
        }
        catch (COMException) { /* a device or session vanished mid-way: skip this round */ }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    // ----- COM declarations. Unused methods are listed only to keep each function in its slot. -----

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorClass { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints();
        [PreserveSig] int GetDefaultAudioEndpoint();
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl();
        [PreserveSig] int GetSimpleAudioVolume();
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState();
        [PreserveSig] int GetDisplayName();
        [PreserveSig] int SetDisplayName();
        [PreserveSig] int GetIconPath();
        [PreserveSig] int SetIconPath();
        [PreserveSig] int GetGroupingParam();
        [PreserveSig] int SetGroupingParam();
        [PreserveSig] int RegisterAudioSessionNotification();
        [PreserveSig] int UnregisterAudioSessionNotification();
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier();
        [PreserveSig] int GetSessionInstanceIdentifier();
        [PreserveSig] int GetProcessId(out uint processId);
    }

    [ComImport, Guid("1C158861-B533-4B30-B1CF-E853E51C59B8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IChannelAudioVolume
    {
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetChannelVolume(uint index, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolume(uint index, out float level);
    }
}
