namespace GlassMixer.Core;

/// <summary>
/// One row in the mixer. Windows can give one program several audio sessions
/// (Chrome opens one per tab process), so sessions from the same .exe are grouped
/// under one Id, the way the Windows volume mixer does it.
/// </summary>
/// <param name="Id">Stable key for the row: the lowercase process name, or "system".</param>
/// <param name="Volume">0.0 to 1.0, the app's own level before the master volume.</param>
/// <param name="Active">True while the app has a stream open and playing (it may still be silent).</param>
/// <param name="Balance">-1 left only, 0 centre, +1 right only.</param>
/// <param name="OutputId">The device this app is sent to, or null when it follows the Windows default.</param>
public sealed record AudioApp(
    string Id,
    string Name,
    int ProcessId,
    string? ExePath,
    float Volume,
    bool Muted,
    bool IsSystem,
    bool Active,
    float Balance,
    string? OutputId);

/// <summary>An output device that is plugged in and enabled.</summary>
public sealed record OutputDevice(string Id, string Name);

/// <summary>Everything the UI needs to draw the mixer at one moment.</summary>
public sealed record MixerSnapshot(
    string DeviceId,
    string DeviceName,
    IReadOnlyList<OutputDevice> Devices,
    float MasterVolume,
    bool MasterMuted,
    IReadOnlyList<AudioApp> Apps,
    bool PerAppOutputSupported);
