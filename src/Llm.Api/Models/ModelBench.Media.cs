using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Api.Chat;
using Llm.Core.Models;

namespace Llm.Api.Models;

/// <summary>
/// The test bench for the media models, each by what it does, every result checked by a rule: a picture model makes a picture
/// as big as asked; a video model a clip; a speech model sound that a speech-to-text model hears back as what it was asked to
/// say; a speech-to-text model hears what a speech model said. Their capacity: how many a minute, one and more at once.
/// </summary>
public sealed partial class ModelBench
{
    /// <summary>What it is asked to say, and to hear back, by language.</summary>
    private static string Sentence(string? language) => language == "fa"
        ? "امروز هوا آفتابی است و ما به پارک می‌رویم."
        : "The quick brown fox jumps over the lazy dog near the river bank.";

    /// <summary>The kinds of model the bench has tests for: chat, and each media mode.</summary>
    public static bool Testable(string mode) => mode == "chat" || MediaModes.All.Contains(mode);

    /// <summary>The words of a text, lower case, without punctuation.</summary>
    private static List<string> Words(string text) => [.. WordPattern().Matches(text.ToLowerInvariant()).Select(m => m.Value)];

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordPattern();

    /// <summary>How many of what was said were heard: a share of its words.</summary>
    public static double Heard(string said, string heard)
    {
        var want = Words(said);
        var got = Words(heard).ToHashSet(StringComparer.Ordinal);
        return want.Count == 0 ? 0 : want.Count(got.Contains) / (double)want.Count;
    }

    /// <summary>A picture's width and height, from its PNG, JPEG or WebP header; null when it is none of them.</summary>
    public static (int Width, int Height)? PictureSize(byte[] b)
    {
        if (b.Length > 24 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G')
        {
            return (BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(20)));
        }
        if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            // JPEG: the frame header (SOF0..SOF15, but not DHT, JPG and DAC) holds its height then width.
            for (var i = 2; i + 9 < b.Length;)
            {
                if (b[i] != 0xFF)
                {
                    return null;
                }
                var marker = b[i + 1];
                var length = (b[i + 2] << 8) | b[i + 3];
                if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                {
                    return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
                }
                i += 2 + length;
            }
            return null;
        }
        if (b.Length > 30 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
        {
            var chunk = System.Text.Encoding.ASCII.GetString(b, 12, 4);
            return chunk switch
            {
                "VP8X" => (1 + (b[24] | (b[25] << 8) | (b[26] << 16)), 1 + (b[27] | (b[28] << 8) | (b[29] << 16))),
                "VP8 " => (BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(26)) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(28)) & 0x3FFF),
                "VP8L" => (1 + (((b[22] & 0x3F) << 8) | b[21]), 1 + (((b[24] & 0x0F) << 10) | (b[23] << 2) | ((b[22] & 0xC0) >> 6))),
                _ => null,
            };
        }
        return null;
    }

    /// <summary>Whether bytes are a video: MP4 or MOV (an ftyp box), WebM or MKV (EBML), an animated WebP or a GIF.</summary>
    public static bool IsVideo(byte[] b) =>
        (b.Length > 12 && b[4] == 'f' && b[5] == 't' && b[6] == 'y' && b[7] == 'p')
        || (b.Length > 4 && b[0] == 0x1A && b[1] == 0x45 && b[2] == 0xDF && b[3] == 0xA3)
        || (b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
        || (b.Length > 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F');

    /// <summary>Whether bytes are sound: MP3 (an ID3 tag or a frame), WAV, Ogg, FLAC or WebM.</summary>
    public static bool IsSound(byte[] b) =>
        b.Length > 4 && ((b[0] == 'I' && b[1] == 'D' && b[2] == '3') || (b[0] == 0xFF && (b[1] & 0xE0) == 0xE0)
            || (b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F') || (b[0] == 'O' && b[1] == 'g' && b[2] == 'g' && b[3] == 'S')
            || (b[0] == 'f' && b[1] == 'L' && b[2] == 'a' && b[3] == 'C') || (b[0] == 0x1A && b[1] == 0x45 && b[2] == 0xDF && b[3] == 0xA3));

    private sealed record MediaScope(GatewayChat Gateway, MediaControl Media, GpuShare Gpu, Llm.Api.Media.SdServer Server, AsyncServiceScope Scope) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Scope.DisposeAsync();
    }

    private MediaScope MediaServices()
    {
        var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        return new MediaScope(sp.GetRequiredService<GatewayChat>(), sp.GetRequiredService<MediaControl>(), sp.GetRequiredService<GpuShare>(),
            sp.GetRequiredService<Llm.Api.Media.SdServer>(), scope);
    }

    /// <summary>A picture made, as the chat's tool makes one: its turn on the GPU, loaded first.</summary>
    private static async Task<byte[]> PictureAsync(MediaScope s, MediaControl.Model model, string prompt, string size, string email)
    {
        await using var hold = await s.Gpu.HoldAsync(model, CancellationToken.None);
        if (await s.Media.ReadyAsync(model.Name, CancellationToken.None) is { } notReady)
        {
            throw new InvalidOperationException(notReady);
        }
        var png = await s.Gateway.GenerateImageAsync(model.Name, prompt, size, email, CancellationToken.None);
        s.Media.Touch(model.Name);
        return png;
    }

    private static async Task<(byte[] Video, string Type)> ClipAsync(MediaScope s, MediaControl.Model model, string prompt, int seconds)
    {
        await using var hold = await s.Gpu.HoldAsync(model, CancellationToken.None);
        if (await s.Media.ReadyAsync(model.Name, CancellationToken.None) is { } notReady)
        {
            throw new InvalidOperationException(notReady);
        }
        var made = await s.Server.VideoAsync(model, prompt, seconds, CancellationToken.None);
        s.Media.Touch(model.Name);
        return made;
    }

    /// <summary>The model that hears (or speaks) for a test of the other: of its language, else any.</summary>
    private static MediaControl.Model? Partner(MediaControl media, string mode, string? language) =>
        media.All.Where(m => m.Mode == mode).OrderBy(m => m.Row.Language == language ? 0 : m.Row.Language is null ? 1 : 2).FirstOrDefault();

    private static JsonObject Result(string id, string title, string skill, bool passed, string? why, TimeSpan took, string answer) => new()
    {
        ["id"] = id, ["title"] = title, ["skill"] = skill, ["passed"] = passed, ["why"] = why, ["ms"] = (int)took.TotalMilliseconds, ["tokens"] = 0, ["answer"] = answer,
    };

    /// <summary>One test: its work timed, its check; an error is its failure, said.</summary>
    private static async Task<JsonObject> TestAsync(string id, string title, string skill, Func<Task<(bool Passed, string? Why, string Answer)>> work)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var (passed, why, answer) = await work();
            return Result(id, title, skill, passed, why, watch.Elapsed, answer);
        }
        catch (Exception e) when (e is ChatGatewayException or HttpRequestException or InvalidOperationException or TaskCanceledException
            or Llm.Api.Media.SdServer.SdException or System.Text.Json.JsonException)
        {
            return Result(id, title, skill, false, e.Message.Length > 400 ? e.Message[..400] : e.Message, watch.Elapsed, "");
        }
    }

    private async Task MediaCapabilityAsync(Guid id, MediaControl.Model model, string email)
    {
        await using var s = MediaServices();
        var results = new JsonArray();
        async Task Add(JsonObject r)
        {
            results.Add(r);
            await SaveAsync(id, new JsonObject { ["tests"] = results.DeepClone(), ["passed"] = results.Count(x => x!["passed"]!.GetValue<bool>()), ["total"] = results.Count }, null, null);
        }
        var language = model.Row.Language;
        switch (model.Mode)
        {
            case MediaModes.Image:
                var (w, h) = (model.Row.Width ?? 1024, model.Row.Height ?? 1024);
                foreach (var (tid, title, size) in new[] { ("picture", "Makes a picture", $"{w}x{h}"), ("size", "As big as asked", $"{Math.Max(256, w / 2 / 64 * 64)}x{Math.Max(256, h / 2 / 64 * 64)}") })
                {
                    await Add(await TestAsync(tid, title, "pictures", async () =>
                    {
                        var png = await PictureAsync(s, model, "A red apple on a white table, studio light", size, email);
                        var want = size.Split('x').Select(int.Parse).ToArray();
                        return PictureSize(png) is not { } got ? (false, $"not a picture ({png.Length:N0} bytes)", "")
                            : got.Width == want[0] && got.Height == want[1] ? (true, null, $"{got.Width}×{got.Height}, {png.Length / 1024:N0} KB")
                            : (false, $"{got.Width}×{got.Height}, not {size.Replace('x', '×')}", $"{got.Width}×{got.Height}");
                    }));
                }
                break;
            case MediaModes.Video:
                await Add(await TestAsync("clip", "Makes a clip", "video", async () =>
                {
                    var (video, type) = await ClipAsync(s, model, "A paper boat floating on a calm pond, slow camera pan", 2);
                    return IsVideo(video) && video.Length > 10_000 ? (true, null, $"{type}, {video.Length / 1024:N0} KB") : (false, $"not a video ({type}, {video.Length:N0} bytes)", type);
                }));
                break;
            case MediaModes.Speech:
                var sentence = Sentence(language);
                var voice = model.Row.DefaultVoice ?? model.Row.Voices.FirstOrDefault() ?? "";
                byte[]? spoken = null;
                await Add(await TestAsync("speak", "Speaks a sentence", "speech", async () =>
                {
                    spoken = await s.Gateway.SpeakAsync(model.Name, sentence, voice, email, CancellationToken.None);
                    return IsSound(spoken) && spoken.Length > 2_000 ? (true, null, $"{spoken.Length / 1024:N0} KB of sound") : (false, $"not sound ({spoken.Length:N0} bytes)", "");
                }));
                var ear = Partner(s.Media, MediaModes.Transcription, language);
                await Add(await TestAsync("clear", "Heard back as said", "speech", async () =>
                {
                    if (spoken is null || !IsSound(spoken))
                    {
                        return (false, "no sound to hear", "");
                    }
                    if (ear is null)
                    {
                        return (false, "no speech-to-text model to hear it with", "");
                    }
                    var heard = await s.Gateway.TranscribeAsync(ear.Name, spoken, "speech.mp3", email, CancellationToken.None, language: language);
                    var share = Heard(sentence, heard);
                    return share >= 0.7 ? (true, null, $"{ear.Name}: {heard}") : (false, $"{share:P0} of its words heard by {ear.Name}", heard);
                }));
                break;
            case MediaModes.Transcription:
                var mouth = Partner(s.Media, MediaModes.Speech, language);
                var said = Sentence(mouth?.Row.Language ?? language);
                await Add(await TestAsync("hear", "Hears a sentence", "speech to text", async () =>
                {
                    if (mouth is null)
                    {
                        return (false, "no speech model to say a sentence for it to hear", "");
                    }
                    var sound = await s.Gateway.SpeakAsync(mouth.Name, said, mouth.Row.DefaultVoice ?? mouth.Row.Voices.FirstOrDefault() ?? "", email, CancellationToken.None);
                    var heard = await s.Gateway.TranscribeAsync(model.Name, sound, "speech.mp3", email, CancellationToken.None, language: mouth.Row.Language);
                    var share = Heard(said, heard);
                    return share >= 0.7 ? (true, null, heard) : (false, $"{share:P0} of the words {mouth.Name} said", heard);
                }));
                break;
        }
    }

    /// <summary>
    /// A media model's capacity: one, then more at once (a picture or video model takes the GPU one at a time, so once),
    /// how many it makes a minute and how long each takes.
    /// </summary>
    private async Task MediaCapacityAsync(Guid id, MediaControl.Model model, string email)
    {
        await using var s = MediaServices();
        var levels = new JsonArray();
        var unit = model.Mode switch { MediaModes.Image => "pictures", MediaModes.Video => "clips", MediaModes.Speech => "sentences", _ => "transcriptions" };
        var language = model.Row.Language;
        // What a speech-to-text model hears: a sentence said once, by the speech model of its language.
        byte[]? sound = null;
        if (model.Mode == MediaModes.Transcription)
        {
            var mouth = Partner(s.Media, MediaModes.Speech, language) ?? throw new InvalidOperationException("No speech model to say what it hears.");
            sound = await s.Gateway.SpeakAsync(mouth.Name, Sentence(mouth.Row.Language ?? language), mouth.Row.DefaultVoice ?? mouth.Row.Voices.FirstOrDefault() ?? "", email, CancellationToken.None);
        }
        Task One() => model.Mode switch
        {
            MediaModes.Image => PictureAsync(s, model, "A lighthouse on a cliff at sunset", $"{model.Row.Width ?? 1024}x{model.Row.Height ?? 1024}", email),
            MediaModes.Video => ClipAsync(s, model, "Waves rolling onto a sandy beach", 2),
            MediaModes.Speech => s.Gateway.SpeakAsync(model.Name, Sentence(language), model.Row.DefaultVoice ?? model.Row.Voices.FirstOrDefault() ?? "", email, CancellationToken.None),
            _ => s.Gateway.TranscribeAsync(model.Name, sound!, "speech.mp3", email, CancellationToken.None, language: language),
        };
        var steps = model.Mode is MediaModes.Image or MediaModes.Video ? new[] { 1 } : [1, 2, 4];
        double best = 0;
        var bestAt = 0;
        foreach (var n in steps)
        {
            var wall = Stopwatch.StartNew();
            var each = new List<double>();
            var failed = 0;
            await Task.WhenAll(Enumerable.Range(0, n).Select(async _ =>
            {
                var w = Stopwatch.StartNew();
                try
                {
                    await One();
                    lock (each)
                    {
                        each.Add(w.Elapsed.TotalSeconds);
                    }
                }
                catch (Exception e) when (e is ChatGatewayException or HttpRequestException or InvalidOperationException or Llm.Api.Media.SdServer.SdException)
                {
                    Interlocked.Increment(ref failed);
                }
            }));
            var took = wall.Elapsed.TotalSeconds;
            var perMinute = took > 0 ? each.Count * 60 / took : 0;
            levels.Add(new JsonObject
            {
                ["atOnce"] = n, ["perMinute"] = Math.Round(perMinute, 2), ["secondsEach"] = Math.Round(each.DefaultIfEmpty(0).Average(), 1), ["failed"] = failed, ["seconds"] = Math.Round(took, 1),
            });
            if (perMinute > best)
            {
                (best, bestAt) = (perMinute, n);
            }
            await SaveAsync(id, new JsonObject { ["unit"] = unit, ["levels"] = levels.DeepClone(), ["best"] = new JsonObject { ["atOnce"] = bestAt, ["perMinute"] = Math.Round(best, 2) } }, null, null);
            if (failed > 0)
            {
                break;
            }
        }
    }
}
