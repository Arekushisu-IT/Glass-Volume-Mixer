using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace GlassMixer.Core;

/// <summary>
/// Talks to the Windows Core Audio API (through NAudio) for the default output device.
///
/// How Windows organises sound:
///   device (MMDevice)            your speakers or headphones; has the master volume
///   └─ session manager           keeps a list of every program playing to that device
///      └─ session (AudioSessionControl)
///           ├─ SimpleAudioVolume       that program's own volume and mute
///           └─ AudioMeterInformation   how loud it is right now (peak, 0.0 to 1.0)
///
/// Use this class from one thread only (the UI thread in the app, Main in the probe).
/// </summary>
public sealed class AudioService : IDisposable
{
    public const string MasterId = "master";
    const string SystemId = "system";

    readonly MMDeviceEnumerator _enumerator = new();
    MMDevice _device;
    Dictionary<string, SessionGroup> _groups = new();
    List<OutputDevice> _devices = new();

    sealed class SessionGroup(string id, string name, int processId, string? exePath, bool isSystem)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public int ProcessId { get; } = processId;
        public string? ExePath { get; } = exePath;
        public bool IsSystem { get; } = isSystem;
        public List<AudioSessionControl> Sessions { get; } = new();
        public HashSet<int> ProcessIds { get; } = new(); // Chrome and Edge run several processes
        public float Balance { get; set; }
        public string? OutputId { get; set; }            // null: follows the Windows default device
    }

    public AudioService()
    {
        _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        Refresh();
    }

    public string DeviceName => _device.FriendlyName;

    public float MasterVolume
    {
        get => _device.AudioEndpointVolume.MasterVolumeLevelScalar;
        set => _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f);
    }

    public bool MasterMuted
    {
        get => _device.AudioEndpointVolume.Mute;
        set => _device.AudioEndpointVolume.Mute = value;
    }

    /// <summary>How loud the whole device is right now, 0.0 to 1.0.</summary>
    public float MasterPeak => Safe(() => _device.AudioMeterInformation.MasterPeakValue);

    /// <summary>
    /// Re-reads the session list from Windows. Call it every second or two:
    /// programs start and stop playing sound all the time.
    /// It also follows the default device if you switch speakers to headphones.
    /// Sessions are read from every output device, so an app sent to another
    /// device (per-app output) stays in the list.
    /// </summary>
    public void Refresh()
    {
        var current = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        if (current.ID != _device.ID) _device = current;

        var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        _devices = devices.Select(d => new OutputDevice(d.ID, d.FriendlyName)).ToList();

        var groups = new Dictionary<string, SessionGroup>();
        foreach (var device in devices)
        {
            var manager = device.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session.State == AudioSessionState.AudioSessionStateExpired) continue;

                var (id, name, pid, path, isSystem) = Identify(session);
                if (!groups.TryGetValue(id, out var group))
                    groups[id] = group = new SessionGroup(id, name, pid, path, isSystem);
                group.Sessions.Add(session);
                if (!isSystem) group.ProcessIds.Add((int)session.GetProcessID);
            }
        }

        var deviceIds = _devices.Select(d => d.Id).ToList();
        foreach (var g in groups.Values.Where(g => !g.IsSystem))
        {
            g.Balance = SessionChannels.GetBalance(deviceIds, g.ProcessIds);
            g.OutputId = AppOutput.Get(g.ProcessId);
        }
        _groups = groups;
    }

    public MixerSnapshot GetSnapshot()
    {
        var apps = _groups.Values
            .Select(g => new AudioApp(
                g.Id, g.Name, g.ProcessId, g.ExePath,
                Volume: Safe(() => g.Sessions[0].SimpleAudioVolume.Volume),
                Muted: g.Sessions.All(s => Safe(() => s.SimpleAudioVolume.Mute)),
                g.IsSystem,
                Active: g.Sessions.Any(s => Safe(() => s.State) == AudioSessionState.AudioSessionStateActive),
                g.Balance,
                g.OutputId))
            .OrderBy(a => a.IsSystem)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new MixerSnapshot(_device.ID, DeviceName, _devices, MasterVolume, MasterMuted, apps, AppOutput.IsSupported);
    }

    /// <summary>Left/right balance for an app: -1 left only, 0 centre, +1 right only.</summary>
    public void SetBalance(string id, float balance)
    {
        if (!_groups.TryGetValue(id, out var g) || g.IsSystem) return;
        SessionChannels.SetBalance(_devices.Select(d => d.Id), g.ProcessIds, balance);
        g.Balance = Math.Clamp(balance, -1f, 1f);
    }

    /// <summary>Sends an app to a specific output device, or with null back to the default.</summary>
    public void SetOutput(string id, string? deviceId)
    {
        if (!_groups.TryGetValue(id, out var g) || g.IsSystem) return;
        foreach (var pid in g.ProcessIds) AppOutput.Set(pid, deviceId);
        g.OutputId = deviceId;
    }

    /// <summary>Makes another device the Windows default output, then reloads its sessions.</summary>
    public void SetDefaultDevice(string deviceId)
    {
        DefaultDevice.Set(deviceId);
        Refresh();
    }

    /// <summary>Current peak level per app id. Cheap: call it 30 times a second for meters.</summary>
    public Dictionary<string, float> GetPeaks()
    {
        var peaks = new Dictionary<string, float>(_groups.Count);
        foreach (var g in _groups.Values)
            peaks[g.Id] = g.Sessions.Max(s => Safe(() => s.AudioMeterInformation.MasterPeakValue));
        return peaks;
    }

    public void SetVolume(string id, float volume)
    {
        volume = Math.Clamp(volume, 0f, 1f);
        if (id == MasterId) { MasterVolume = volume; return; }
        if (!_groups.TryGetValue(id, out var g)) return;
        foreach (var s in g.Sessions) Safe(() => s.SimpleAudioVolume.Volume = volume);
    }

    public void SetMute(string id, bool muted)
    {
        if (id == MasterId) { MasterMuted = muted; return; }
        if (!_groups.TryGetValue(id, out var g)) return;
        foreach (var s in g.Sessions) Safe(() => s.SimpleAudioVolume.Mute = muted);
    }

    /// <summary>Works out which program owns a session and what to call it.</summary>
    static (string Id, string Name, int Pid, string? Path, bool IsSystem) Identify(AudioSessionControl session)
    {
        if (session.IsSystemSoundsSession) return (SystemId, "System sounds", 0, null, true);

        int pid = (int)session.GetProcessID;
        try
        {
            using var process = Process.GetProcessById(pid);
            string name = process.ProcessName;
            string? path = null;
            try
            {
                path = process.MainModule?.FileName;
                if (path is not null)
                {
                    // "Google Chrome" reads better than "chrome".
                    var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                    if (!string.IsNullOrWhiteSpace(description)) name = description;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Elevated or protected processes hide their path. Keep the plain process name.
            }

            // Some apps set their own session name. Names starting with '@' are resource ids, not text.
            var display = session.DisplayName;
            if (!string.IsNullOrWhiteSpace(display) && !display.StartsWith('@')) name = display;

            return (process.ProcessName.ToLowerInvariant(), name, pid, path, false);
        }
        catch (ArgumentException)
        {
            return ($"pid-{pid}", $"Process {pid}", pid, null, false); // the process already exited
        }
    }

    // A session can disappear between two calls (the app closed). Windows then
    // throws a COMException; treat that as "silent" instead of crashing the mixer.
    static T Safe<T>(Func<T> read)
    {
        try { return read(); }
        catch (COMException) { return default!; }
        catch (InvalidCastException) { return default!; }
    }

    static void Safe(Action write)
    {
        try { write(); }
        catch (COMException) { }
        catch (InvalidCastException) { }
    }

    public void Dispose()
    {
        _groups.Clear();
        _enumerator.Dispose();
    }
}
