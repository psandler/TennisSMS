using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

// Records scripted walkthroughs of the app as videos, with a drawn pointer, spotlights and captions.
// Development only: every recording resets the dev database. Today there's one recording, the tour.
//
//   dotnet run --project tools/TennisSms.ScenarioRecorder                  (everything)
//   dotnet run --project tools/TennisSms.ScenarioRecorder -- --list        (what can be recorded)
//   dotnet run --project tools/TennisSms.ScenarioRecorder -- tour --headed
//   dotnet run --project tools/TennisSms.ScenarioRecorder -- tour --pace 1.3 --mp4

var options = RecorderOptions.Parse(args);
if (options is null) return 1;

var root = Repo.FindRoot();
var outDir = Path.GetFullPath(Path.Combine(root, options.OutDir));

// MP4 needs a full ffmpeg (Playwright's bundled one only writes WebM). Check before spending minutes recording.
string? ffmpeg = null;
if (options.Mp4 && (ffmpeg = Mp4.FindFfmpeg()) is null)
{
    Console.Error.WriteLine("""
        --mp4 needs ffmpeg on the PATH, and it isn't there. Install it, open a new terminal, and try again:
          winget install Gyan.FFmpeg
        Or record without --mp4 and convert later.
        """);
    return 1;
}

Console.WriteLine("Making sure Playwright's Chromium is installed (first run downloads it)...");
var installExit = Microsoft.Playwright.Program.Main(["install", "chromium"]);
if (installExit != 0)
{
    Console.Error.WriteLine($"Playwright browser install failed (exit {installExit}).");
    return 1;
}

await using var app = await AppProcess.EnsureRunningAsync(options.Url, root);
using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = !options.Headed });

var recorder = new Recorder(browser, options.Url, outDir, options.Pace);
var available = Recorder.Available;

if (options.List)
{
    Console.WriteLine("Recordings:");
    foreach (var r in available) Console.WriteLine($"  {r.Name,-24} {r.Description}");
    return 0;
}

var selected = options.Names.Count == 0
    ? available
    : options.Names.Select(n => available.FirstOrDefault(r => r.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                                ?? throw new ArgumentException($"No recording named '{n}'. Use --list.")).ToList();

Directory.CreateDirectory(outDir);
foreach (var recording in selected)
{
    Console.WriteLine($"Recording '{recording.Name}'...");
    var (file, clips) = await recorder.RecordAsync(recording);
    Console.WriteLine($"  saved {file}");
    if (ffmpeg is not null)
        Console.WriteLine($"  saved {await Mp4.ConvertAsync(ffmpeg, file, clips)}{(clips.Count > 0 ? $" (with {clips.Count} voice clips)" : "")}");
    else if (clips.Count > 0)
        Console.WriteLine("  (this recording has a voiceover; add --mp4 to get it, the WebM is silent)");
}

try { Directory.Delete(Path.Combine(outDir, ".tmp"), recursive: true); } catch (IOException) { }
Console.WriteLine($"Done. Videos are in {outDir}");
return 0;

/// <summary>Something to record: a name for the command line and the files, and the script that plays it.</summary>
sealed record Recording(string Name, string Description, Func<Stage, Task> Play);

sealed class Recorder(IBrowser browser, string baseUrl, string outDir, double pace)
{
    private const int Width = 1600, Height = 1000;


    /// <summary>Every recording. Add new scripts here (each in its own file, like Tour.cs).</summary>
    public static readonly IReadOnlyList<Recording> Available =
    [
        new("tour", "A guided tour: players opt in, the organizer creates a game, replies by text and by link, alerts, a drop and a refill, a reminder",
            stage => new Tour(stage).RunAsync()),
        new("opening", "Just the tour's opening (the simulator explained, players signing up), for quick reviews",
            stage => new Tour(stage).OpeningAsync()),
        new("samples", "Short samples of presentation ideas: clean layout, close-ups, camera zoom, side by side, voiceover (use --mp4 to hear it)",
            stage => new Samples(stage).RunAsync()),
    ];

    public async Task<(string File, IReadOnlyList<VoiceClip> Clips)> RecordAsync(Recording recording)
    {
        var videoTemp = Path.Combine(outDir, ".tmp");
        await using var context = await browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = Width, Height = Height },
            RecordVideoDir = videoTemp,
            RecordVideoSize = new() { Width = Width, Height = Height },
        });
        await context.AddInitScriptAsync(Stage.OverlayScript);
        var page = await context.NewPageAsync();
        var stage = new Stage(page, browser, baseUrl, pace)
        {
            Voice = new Narrator(Path.Combine(videoTemp, $"voice-{Slug(recording.Name)}")),
        };

        await recording.Play(stage);

        // Timestamped, so earlier takes are kept for comparison: tour-2026-11-14-0930.webm
        var target = Path.Combine(outDir, $"{Slug(recording.Name)}-{DateTime.Now:yyyy-MM-dd-HHmm}.webm");
        var video = page.Video!;
        await context.CloseAsync(); // the video is finished when the context closes
        await video.SaveAsAsync(target);
        await video.DeleteAsync();
        return (target, stage.Clips);
    }

    private static string Slug(string name) =>
        Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
}

sealed record RecorderOptions(string Url, string OutDir, bool Headed, bool List, List<string> Names, double Pace = 1.0, bool Mp4 = false)
{
    public static RecorderOptions? Parse(string[] args)
    {
        var o = new RecorderOptions("http://localhost:5234", "recordings", false, false, []);
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--url" when i + 1 < args.Length: o = o with { Url = args[++i] }; break;
                case "--out" when i + 1 < args.Length: o = o with { OutDir = args[++i] }; break;
                case "--headed": o = o with { Headed = true }; break;
                case "--list": o = o with { List = true }; break;
                case "--mp4": o = o with { Mp4 = true }; break;
                case "--pace" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], System.Globalization.CultureInfo.InvariantCulture, out var pace) || pace is < 0.25 or > 4)
                    {
                        Console.Error.WriteLine("--pace takes a number from 0.25 to 4 (1 = normal, 1.5 = slower, 0.8 = faster).");
                        return null;
                    }
                    o = o with { Pace = pace };
                    break;
                case "-h" or "--help":
                    Console.WriteLine("""
                        Records scripted walkthroughs of the app as videos (one .webm each).
                        Every recording RESETS the dev database.

                        Usage: dotnet run --project tools/TennisSms.ScenarioRecorder -- [names...] [options]
                          names        recordings to make (default: all). See --list.
                          --list       list the recordings and exit
                          --url URL    the running app (default http://localhost:5234); started if not running
                          --out DIR    output folder, relative to the repo root (default recordings)
                          --headed     show the browser while recording
                          --pace N     stretch every pause, glide and typing delay by N (default 1; 1.5 = slower, 0.8 = faster)
                          --mp4        also save an .mp4 copy (H.264) for sharing; needs ffmpeg on the PATH

                        Files are named <recording>-<yyyy-MM-dd-HHmm>.webm, so earlier takes are kept.
                        """);
                    return null;
                default: o.Names.Add(args[i]); break;
            }
        }
        return o;
    }
}

/// <summary>WebM to MP4 (H.264) with an installed ffmpeg: plays everywhere, including iPhone, Mac, PowerPoint and chat apps.</summary>
static class Mp4
{
    public static string? FindFfmpeg()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "ffmpeg.exe" } : ["ffmpeg"];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in names)
            {
                var path = Path.Combine(dir.Trim(), name);
                if (File.Exists(path)) return path;
            }
        return null;
    }

    public static async Task<string> ConvertAsync(string ffmpeg, string webm, IReadOnlyList<VoiceClip> clips)
    {
        var mp4 = Path.ChangeExtension(webm, ".mp4");
        // CRF 20 / slow: visually close to the source and much smaller. yuv420p and faststart for broad playback and streaming.
        List<string> args = ["-loglevel", "error", "-y", "-i", webm];
        foreach (var c in clips) args.AddRange(["-i", c.Wav]);
        args.AddRange(["-c:v", "libx264", "-preset", "slow", "-crf", "20", "-pix_fmt", "yuv420p", "-movflags", "+faststart"]);
        if (clips.Count == 0) args.Add("-an");
        else
        {
            // Each clip delayed to its moment, then all mixed into one track (without lowering their volume).
            var delays = string.Concat(clips.Select((c, i) => $"[{i + 1}:a]adelay={c.AtMs}|{c.AtMs}[a{i}];"));
            var inputs = string.Concat(clips.Select((_, i) => $"[a{i}]"));
            args.AddRange(["-filter_complex", $"{delays}{inputs}amix=inputs={clips.Count}:normalize=0[voice]",
                           "-map", "0:v", "-map", "[voice]", "-c:a", "aac", "-b:a", "160k"]);
        }
        args.Add(mp4);
        using var process = Process.Start(new ProcessStartInfo(ffmpeg, args)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var errors = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"ffmpeg failed (exit {process.ExitCode}): {errors}");
        return mp4;
    }
}

static class Repo
{
    public static string FindRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "TennisSms.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Run this from inside the TennisSMS repository.");
    }
}

/// <summary>Uses the app if it's already running (for example from Visual Studio); otherwise starts it and stops it afterwards.</summary>
sealed class AppProcess : IAsyncDisposable
{
    private Process? _process;

    public static async Task<AppProcess> EnsureRunningAsync(string url, string root)
    {
        var app = new AppProcess();
        if (await IsUpAsync(url))
        {
            Console.WriteLine($"Using the app already running at {url}.");
            return app;
        }

        Console.WriteLine($"Starting the app ({url})...");
        app._process = Process.Start(new ProcessStartInfo("dotnet",
            ["run", "--project", Path.Combine(root, "src", "TennisSms.Web"), "--launch-profile", "http"])
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        app._process.OutputDataReceived += (_, _) => { };
        app._process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine("  app: " + e.Data); };
        app._process.BeginOutputReadLine();
        app._process.BeginErrorReadLine();

        for (var i = 0; i < 240; i++)
        {
            if (app._process.HasExited) throw new InvalidOperationException("The app exited during startup.");
            if (await IsUpAsync(url)) return app;
            await Task.Delay(500);
        }
        throw new TimeoutException("The app didn't start within two minutes.");
    }

    private static async Task<bool> IsUpAsync(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try { return (await http.GetAsync(url.TrimEnd('/') + "/dev/simulator")).IsSuccessStatusCode; }
        catch { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is null || _process.HasExited) return;
        _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync();
    }
}
