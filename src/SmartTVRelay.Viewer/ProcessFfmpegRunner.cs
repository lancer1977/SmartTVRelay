using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace SmartTVRelay.Viewer;

public sealed class ProcessFfmpegRunner(IOptions<ViewerOptions> options) : IFfmpegRunner
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
            "-nostdin", "-loglevel", "warning", "-i", info.InputUrl,
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
        return new Handle(process);
    }

    private sealed class Handle(Process process) : IFfmpegProcess
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
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }

        public void Dispose()
        {
            Kill();
            process.Dispose();
        }
    }
}
