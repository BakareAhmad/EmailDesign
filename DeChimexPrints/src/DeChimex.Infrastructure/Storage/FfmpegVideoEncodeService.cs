using System.Diagnostics;
using System.Text;
using DeChimex.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;

namespace DeChimex.Infrastructure.Storage;

/// <summary>
/// Re-encodes/remuxes gallery videos to H.264 + fast start so every browser (<paramref cref="video"/>)
/// can decode them. Browsers commonly reject MPEG-4 Part 2 (mp4v), HEVC, etc. with
/// "DEMUXER_ERROR_NO_SUPPORTED_STREAMS", which is exactly why an upload "shows a play button but
/// never plays". If no ffmpeg/ffprobe binary is available (or it fails), the original file is left
/// untouched and UploadAsync still succeeds — the player's inline error message covers that case.
/// </summary>
public sealed class FfmpegVideoEncodeService : IVideoEncodeService
{
    private static readonly string[] CommonFfmpegPaths =
    {
        "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/opt/homebrew/bin/ffmpeg",
        "/usr/bin/ffmpeg.exe", "C:\\ffmpeg\\bin\\ffmpeg.exe"
    };

    private readonly ILogger<FfmpegVideoEncodeService> _logger;
    private string? _ffmpeg;
    private string? _ffprobe;
    private bool _discovered;

    public FfmpegVideoEncodeService(ILogger<FfmpegVideoEncodeService> logger) => _logger = logger;

    private async Task<(string ffmpeg, string ffprobe)?> DiscoverAsync(CancellationToken ct)
    {
        if (_discovered) return (_ffmpeg!, _ffprobe!) == (null!, null!) ? null : (_ffmpeg!, _ffprobe!);

        _discovered = true;
        foreach (var candidate in CommonFfmpegPaths)
        {
            if (File.Exists(candidate)) { _ffmpeg = candidate; break; }
        }

        // Fall back to PATH lookup for both binaries.
        if (string.IsNullOrEmpty(_ffmpeg))
            _ffmpeg = await ResolveOnPathAsync("ffmpeg", ct);
        if (string.IsNullOrEmpty(_ffprobe))
            _ffprobe = await ResolveOnPathAsync("ffprobe", ct);

        if (string.IsNullOrEmpty(_ffmpeg) || string.IsNullOrEmpty(_ffprobe))
        {
            _logger.LogInformation("ffmpeg/ffprobe not found; video re-encoding disabled (original file kept).");
            return null;
        }

        _logger.LogInformation("Video re-encoding enabled via {Ffmpeg}.", _ffmpeg);
        return (_ffmpeg, _ffprobe);
    }

    private static async Task<string?> ResolveOnPathAsync(string name, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/which",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add(name);
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            var path = output.Trim();
            return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> MakeBrowserPlayableAsync(string sourceFile, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(sourceFile)) return false;
            var tools = await DiscoverAsync(ct);
            if (tools is null) return false;
            var (ffmpeg, ffprobe) = tools.Value;

            // Determine the real codec so we only re-encode when needed.
            var codec = await ProbeVideoCodecAsync(ffprobe, sourceFile, ct);
            if (codec is null)
            {
                _logger.LogWarning("Could not probe codec for {File}; leaving original untouched.", sourceFile);
                return false;
            }

            // Already a codec every browser decodes (vp8/vp9/av1/h264): just ensure fast start.
            if (codec is "h264" or "vp8" or "vp9" or "av1")
            {
                // h264 needs the moov box before the mdat so the browser can show duration and
                // seek the progress bar. Skip the rewrite when already fast-start (idempotent).
                if (codec == "h264")
                {
                    if (IsFastStart(sourceFile))
                    {
                        _logger.LogDebug("{File} is already fast-start H.264; leaving untouched.", sourceFile);
                        return false;
                    }
                    return await RemuxFastStartAsync(ffmpeg, sourceFile, ct);
                }
                return false;
            }

            _logger.LogInformation("Gallery video {File} uses unsupported codec '{Codec}'; re-encoding to H.264.", sourceFile, codec);
            return await TranscodeToH264Async(ffmpeg, sourceFile, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Video re-encoding failed for {File}; keeping original.", sourceFile);
            return false;
        }
    }

    private static async Task<string?> ProbeVideoCodecAsync(string ffprobe, string file, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffprobe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-select_streams"); psi.ArgumentList.Add("v:0");
        psi.ArgumentList.Add("-show_entries"); psi.ArgumentList.Add("stream=codec_name");
        psi.ArgumentList.Add("-of"); psi.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
        psi.ArgumentList.Add(file);

        using var proc = Process.Start(psi);
        if (proc is null) return null;
        var output = (await proc.StandardOutput.ReadToEndAsync(ct)).Trim();
        await proc.WaitForExitAsync(ct);
        return string.IsNullOrWhiteSpace(output) ? null : output.ToLowerInvariant();
    }

    private static async Task<bool> RemuxFastStartAsync(string ffmpeg, string file, CancellationToken ct)
    {
        var temp = file + ".faststart.tmp.mp4";
        var args = new List<string> { "-y", "-i", file, "-c", "copy", "-movflags", "+faststart", temp };
        var ok = await RunAsync(ffmpeg, args, ct);
        if (!ok) return false;
        TryReplace(file, temp);
        return true;
    }

    private static async Task<bool> TranscodeToH264Async(string ffmpeg, string file, CancellationToken ct)
    {
        var temp = file + ".h264.tmp.mp4";
        var args = new List<string>
        {
            "-y", "-i", file,
            "-map", "0:v:0",               // video only (drop audio; keeps uploads light & consistent)
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-crf", "23",
            "-pix_fmt", "yuv420p",
            "-movflags", "+faststart",
            temp
        };
        var ok = await RunAsync(ffmpeg, args, ct);
        if (!ok) return false;
        TryReplace(file, temp);
        return true;
    }

    /// <summary>
    /// Fast-start MP4s place the moov (index) box before the mdat (media data) box so the browser
    /// can read the duration/timeline up front (needed for the progress bar and seeking).
    /// </summary>
    private static bool IsFastStart(string file)
    {
        try
        {
            using var fs = File.OpenRead(file);
            var buffer = new byte[1024 * 1024];
            var read = fs.Read(buffer, 0, buffer.Length);
            var span = buffer.AsSpan(0, read);

            int moov = -1, mdat = -1;
            for (var i = 0; i + 4 <= span.Length; i++)
            {
                if (span[i] == (byte)'m' && span[i + 1] == (byte)'o' && span[i + 2] == (byte)'o' && span[i + 3] == (byte)'v')
                {
                    if (moov < 0) moov = i;
                }
                else if (span[i] == (byte)'m' && span[i + 1] == (byte)'d' && span[i + 2] == (byte)'a' && span[i + 3] == (byte)'t')
                {
                    if (mdat < 0) mdat = i;
                }
                if (mdat >= 0 && moov >= 0 && moov < mdat) return true;
            }

            // If we only saw mdat in the header window, the moov is at the end (not fast start).
            if (mdat >= 0 && moov < 0) return false;
            // Ambiguous / inconclusive: don't re-encode, just treat as non-fast-start (safe).
            return moov >= 0 && moov < mdat;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> RunAsync(string ffmpeg, List<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi);
        if (proc is null) return false;
        var err = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) err.AppendLine(e.Data); };
        proc.BeginErrorReadLine();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        await stdoutTask;
        return proc.ExitCode == 0;
    }

    private static void TryReplace(string original, string temp)
    {
        try
        {
            File.Move(temp, original, overwrite: true);
        }
        catch
        {
            if (File.Exists(original)) File.Delete(original);
            File.Move(temp, original);
        }
    }
}
