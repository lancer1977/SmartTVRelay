using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace SmartTVRelay.Viewer;

public sealed class ProcessFfmpegRunner(IOptions<ViewerOptions> options, IHttpClientFactory clients) : IFfmpegRunner
{
    public IFfmpegProcess Start(FfmpegStartInfo info)
    {
        var psi = new ProcessStartInfo(options.Value.FfmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[]
        {
            "-nostdin", "-loglevel", "warning",
            "-probesize", options.Value.ProbeSize,
            "-analyzeduration", options.Value.AnalyzeDurationUs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-fflags", "+genpts+discardcorrupt",
            // The tuner is opened once by the raw transport tap. ffmpeg reads that same
            // stream from stdin while the tap retains bounded original MPEG-TS evidence.
            "-i", "pipe:0",
            "-vf", "yadif", "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
            "-force_key_frames", "expr:gte(t,n_forced*4)",
            "-c:a", "aac", "-b:a", "128k", "-ac", "2",
            "-f", "hls", "-hls_time", "4", "-hls_list_size", "6",
            "-hls_flags", "delete_segments+omit_endlist",
            "-hls_segment_filename", Path.Combine(info.OutputDirectory, "seg%05d.ts"),
            info.PlaylistPath,
        })
        {
            psi.ArgumentList.Add(a);
        }

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        // Drain output so ffmpeg never blocks on a full pipe.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var cancellation = new CancellationTokenSource();
        var pump = PumpAsync(process, info, cancellation.Token);
        return new Handle(process, cancellation, pump);
    }

    private async Task PumpAsync(Process process, FfmpegStartInfo info, CancellationToken cancellationToken)
    {
        try
        {
            using var client = clients.CreateClient("viewer-stream");
            using var response = await client.GetAsync(
                info.InputUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await new RawTransportTap().CopyAsync(
                source, process.StandardInput.BaseStream, info.OutputDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception)
        {
            // An unavailable source ends ffmpeg's input; the viewer reports a startup
            // failure and retains no evidence. Do not log the upstream URL or error text.
        }
        finally
        {
            try { process.StandardInput.Close(); } catch (Exception) { }
        }
    }

    private sealed class Handle(Process process, CancellationTokenSource cancellation, Task pump) : IFfmpegProcess
    {
        public bool HasExited
        {
            get
            {
                try { return process.HasExited; } catch (InvalidOperationException) { return true; }
            }
        }

        public void Kill()
        {
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }

        public void Dispose()
        {
            Kill();
            try { pump.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { }
            cancellation.Dispose();
            process.Dispose();
        }
    }
}
