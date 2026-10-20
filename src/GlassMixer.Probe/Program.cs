// Step 1 of Glass Mixer: prove we can read and change real app volumes
// before building any window. Run it with:
//
//   dotnet run --project src/GlassMixer.Probe
//   dotnet run --project src/GlassMixer.Probe -- set chrome 40
//   dotnet run --project src/GlassMixer.Probe -- mute spotify
//   dotnet run --project src/GlassMixer.Probe -- unmute spotify
//   dotnet run --project src/GlassMixer.Probe -- balance spotify -50      (-100 left ... 100 right)
//   dotnet run --project src/GlassMixer.Probe -- output spotify 2         (device number from the list)
//   dotnet run --project src/GlassMixer.Probe -- output spotify default
//
// Play some sound (music, a video) first so there is something to see.

using GlassMixer.Core;

using var audio = new AudioService();

if (args.Length >= 2)
{
    var (command, id) = (args[0].ToLowerInvariant(), args[1].ToLowerInvariant());
    switch (command)
    {
        case "set" when args.Length >= 3 && int.TryParse(args[2], out var percent):
            audio.SetVolume(id, percent / 100f);
            Console.WriteLine($"Set {id} to {percent}%");
            break;
        case "mute":
            audio.SetMute(id, true);
            Console.WriteLine($"Muted {id}");
            break;
        case "unmute":
            audio.SetMute(id, false);
            Console.WriteLine($"Unmuted {id}");
            break;
        case "balance" when args.Length >= 3 && int.TryParse(args[2], out var balance):
            audio.SetBalance(id, balance / 100f);
            Console.WriteLine($"Set {id} balance to {balance}");
            break;
        case "output" when args.Length >= 3:
            var devices = audio.GetSnapshot().Devices;
            string? target = args[2] == "default" ? null
                : int.TryParse(args[2], out var n) && n >= 1 && n <= devices.Count ? devices[n - 1].Id : null;
            audio.SetOutput(id, target);
            Console.WriteLine($"Sent {id} to {(target is null ? "the default device" : devices.First(d => d.Id == target).Name)}");
            break;
        default:
            Console.WriteLine("Commands: set <id> <0-100> | mute <id> | unmute <id> | balance <id> <-100..100> | output <id> <n|default>");
            return;
    }
    audio.Refresh();
    Console.WriteLine();
}

var snap = audio.GetSnapshot();
Console.WriteLine($"Default device : {snap.DeviceName}");
Console.WriteLine($"Master volume  : {snap.MasterVolume * 100:0}%{(snap.MasterMuted ? "  (muted)" : "")}");
Console.WriteLine($"Per-app output : {(snap.PerAppOutputSupported ? "supported" : "not available on this Windows")}");
Console.WriteLine();
Console.WriteLine("Devices:");
for (int i = 0; i < snap.Devices.Count; i++) Console.WriteLine($"  {i + 1}. {snap.Devices[i].Name}");
Console.WriteLine();
Console.WriteLine($"{"ID",-18}{"NAME",-28}{"PID",7}{"VOLUME",8}{"BALANCE",9}  OUTPUT");
foreach (var app in snap.Apps)
{
    var name = app.Name.Length > 26 ? app.Name[..25] + "…" : app.Name;
    var output = app.OutputId is null ? "default" : snap.Devices.FirstOrDefault(d => d.Id == app.OutputId)?.Name ?? app.OutputId;
    Console.WriteLine($"{app.Id,-18}{name,-28}{app.ProcessId,7}{app.Volume * 100,7:0}%{app.Balance * 100,9:0}  {output}{(app.Muted ? "  muted" : "")}");
}

Console.WriteLine();
Console.WriteLine("Live levels (6 samples, half a second apart):");
for (int i = 0; i < 6; i++)
{
    var peaks = audio.GetPeaks();
    var line = string.Join("  ", peaks
        .Where(p => p.Value > 0.001f)
        .Select(p => $"{p.Key} {Bar(p.Value)}"));
    Console.WriteLine($"  master {Bar(audio.MasterPeak)}  {(line.Length > 0 ? line : "(no app is making sound)")}");
    Thread.Sleep(500);
}

static string Bar(float level) => "[" + new string('#', (int)Math.Round(level * 10)).PadRight(10) + "]";
