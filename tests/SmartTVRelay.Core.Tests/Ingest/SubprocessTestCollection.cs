namespace SmartTVRelay.Core.Tests.Ingest;

using Xunit;

/// <summary>
/// Serializes every test class that spawns a real ffmpeg/ffprobe subprocess. Some of those
/// tests assert against system-wide process state (e.g. "no leaked ffmpeg processes"), which is
/// inherently racy if another test class is concurrently spawning/killing its own ffmpeg
/// processes in the default per-class-parallel xunit execution model -- confirmed by
/// reproducing FrameAudioSamplerTests.SampleAudioAsync_CompleteEnumeration_NoProcessLeaks
/// flaking only once a second ffmpeg-spawning test class (CaptionExtractorTests) ran alongside
/// it, and not at all in isolation or before that class existed.
/// </summary>
[CollectionDefinition("Subprocess", DisableParallelization = true)]
public class SubprocessTestCollection
{
}
