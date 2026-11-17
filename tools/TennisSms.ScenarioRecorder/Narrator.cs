using System.Speech.Synthesis;

/// <summary>A voice clip placed in the video: it starts <see cref="AtMs"/> after the video does.</summary>
sealed record VoiceClip(int AtMs, string Wav);

/// <summary>A spoken line saved as a WAV file, and how long it lasts.</summary>
sealed record SpokenLine(string Path, int Ms);

/// <summary>
/// Voiceovers with Windows' built-in text to speech (no network, no account). Each line becomes a WAV
/// file; the recorder mixes them into the MP4 at the moments they were spoken. The voice is set here.
/// </summary>
sealed class Narrator(string dir, string voice = "Microsoft Zira Desktop", int rate = 0)
{
    private readonly Dictionary<string, SpokenLine> _lines = [];

    /// <summary>Makes the clips ahead of time, so speaking them during the recording doesn't pause the video.</summary>
    public async Task PrepareAsync(IEnumerable<string> lines)
    {
        foreach (var line in lines) await GetAsync(line);
    }

    public Task<SpokenLine> GetAsync(string text)
    {
        if (_lines.TryGetValue(text, out var done)) return Task.FromResult(done);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Voiceovers use Windows text to speech.");

        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"line-{_lines.Count:000}.wav");
        using (var synth = new SpeechSynthesizer())
        {
            synth.SelectVoice(voice);
            synth.Rate = rate; // -10 (slow) to 10 (fast)
            synth.SetOutputToWaveFile(path);
            synth.Speak(text);
        }
        return Task.FromResult(_lines[text] = new SpokenLine(path, WavMs(path)));
    }

    /// <summary>Length of a PCM WAV file: the data chunk's size over the bytes per second.</summary>
    private static int WavMs(string path)
    {
        var b = File.ReadAllBytes(path);
        var byteRate = BitConverter.ToInt32(b, 28);
        for (var i = 12; i + 8 <= b.Length; i += 8 + BitConverter.ToInt32(b, i + 4))
            if (b[i] == 'd' && b[i + 1] == 'a' && b[i + 2] == 't' && b[i + 3] == 'a')
                return (int)(BitConverter.ToInt32(b, i + 4) * 1000L / byteRate);
        return (int)((b.Length - 44) * 1000L / byteRate);
    }
}
