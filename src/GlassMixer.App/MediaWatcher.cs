using Windows.Media.Control;
using Windows.Storage.Streams;

namespace GlassMixer.App;

/// <summary>
/// Now Playing: what Spotify, a browser tab, the Media Player and so on are playing, with
/// play/pause and next/previous.
///
/// Apps publish this through the System Media Transport Controls: the same information the
/// Windows volume flyout and the lock screen show. GlobalSystemMediaTransportControlsSessionManager
/// (from the Windows Runtime; the "10.0.19041" in the project's target makes it available)
/// lists every app that does, and tells us when something changes.
/// </summary>
sealed class MediaWatcher
{
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    readonly Dictionary<string, Track> _tracks = new(); // per app, the last song we read
    readonly HashSet<GlobalSystemMediaTransportControlsSession> _watched = new();

    /// <summary>One app's media, shaped for the page.</summary>
    public sealed record Track(
        string Id, string Title, string Artist, string? Art,
        bool Playing, double Position, double Duration, bool CanPrev, bool CanNext);

    /// <summary>Something changed. Raised on a background thread; call Snapshot from the UI thread.</summary>
    public event Action? Changed;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (_, _) => { WatchSessions(); Changed?.Invoke(); };
            _manager.CurrentSessionChanged += (_, _) => Changed?.Invoke();
            WatchSessions();
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Write("Now Playing is not available: " + ex.Message);
        }
    }

    void WatchSessions()
    {
        if (_manager is null) return;
        foreach (var s in _manager.GetSessions())
        {
            lock (_watched) { if (!_watched.Add(s)) continue; }
            s.MediaPropertiesChanged += (session, args) => { _ = ReadAsync(session); };
            s.PlaybackInfoChanged += (_, _) => Changed?.Invoke();
            s.TimelinePropertiesChanged += (_, _) => Changed?.Invoke();
            _ = ReadAsync(s);
        }
    }

    // Title, artist and cover need an async call, so they're read when they change and kept.
    async Task ReadAsync(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var p = await session.TryGetMediaPropertiesAsync();
            string id = session.SourceAppUserModelId;
            string? art = null;
            lock (_tracks)
            {
                // Same song as before: keep the cover we already read.
                if (_tracks.TryGetValue(id, out var old) && old.Title == p.Title && old.Artist == p.Artist) art = old.Art;
            }
            art ??= await ReadThumbnailAsync(p.Thumbnail);
            lock (_tracks) _tracks[id] = new Track(id, p.Title ?? "", p.Artist ?? "", art, false, 0, 0, false, false);
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Write("Now Playing read failed: " + ex.Message);
        }
    }

    static async Task<string?> ReadThumbnailAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null) return null;
        using var stream = await thumbnail.OpenReadAsync();
        if (stream.Size == 0 || stream.Size > 2_000_000) return null;
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        var bytes = new byte[stream.Size];
        reader.ReadBytes(bytes);
        string type = string.IsNullOrEmpty(stream.ContentType) ? "image/png" : stream.ContentType;
        return $"data:{type};base64,{Convert.ToBase64String(bytes)}";
    }

    /// <summary>Every app with media right now, the one Windows considers current first.</summary>
    public (List<Track> Tracks, string? Current) Snapshot()
    {
        var list = new List<Track>();
        if (_manager is null) return (list, null);
        string? current = null;
        try
        {
            current = _manager.GetCurrentSession()?.SourceAppUserModelId;
            foreach (var s in _manager.GetSessions())
            {
                Track? t;
                lock (_tracks) _tracks.TryGetValue(s.SourceAppUserModelId, out t);
                if (t is null || t.Title.Length == 0) continue;
                var info = s.GetPlaybackInfo();
                var time = s.GetTimelineProperties();
                bool playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                // The position is only updated now and then; add the time since, while playing.
                double position = time.Position.TotalSeconds;
                if (playing && time.LastUpdatedTime.Year > 2000)
                    position += (DateTimeOffset.Now - time.LastUpdatedTime).TotalSeconds * (info.PlaybackRate ?? 1);
                double duration = (time.EndTime - time.StartTime).TotalSeconds;
                list.Add(t with
                {
                    Playing = playing,
                    Position = Math.Max(0, duration > 0 ? Math.Min(position, duration) : position),
                    Duration = Math.Max(0, duration),
                    CanPrev = info.Controls.IsPreviousEnabled,
                    CanNext = info.Controls.IsNextEnabled,
                });
            }
        }
        catch (Exception ex)
        {
            Log.Write("Now Playing snapshot failed: " + ex.Message);
        }
        return (list, current);
    }

    /// <summary>action: "toggle", "next" or "prev".</summary>
    public async Task CommandAsync(string id, string action)
    {
        var s = _manager?.GetSessions().FirstOrDefault(x => x.SourceAppUserModelId == id);
        if (s is null) return;
        try
        {
            _ = action switch
            {
                "toggle" => await s.TryTogglePlayPauseAsync(),
                "next" => await s.TrySkipNextAsync(),
                "prev" => await s.TrySkipPreviousAsync(),
                _ => false,
            };
        }
        catch (Exception ex)
        {
            Log.Write($"Media {action} failed: " + ex.Message);
        }
    }
}
