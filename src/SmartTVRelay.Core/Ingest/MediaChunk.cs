namespace SmartTVRelay.Core.Ingest;

/// <summary>One bounded chunk of raw transport-stream bytes read from a broadcast source, with a
/// source-relative capture timestamp. Sources emit raw bytes only -- they do not decode, interpret,
/// or classify content (AGENTS.md: ingest is a separate layer from detection).</summary>
public sealed record MediaChunk(ReadOnlyMemory<byte> Data, TimeSpan SourceTime);
