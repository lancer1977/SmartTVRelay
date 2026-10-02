using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SmartTVRelay.Core.Vlm;

/// <summary>
/// Calls a locally-running Ollama server as the local VLM adapter for #43. Bounded on every axis
/// the issue requires: a fixed, non-caller-controllable prompt (only the image varies), a capped
/// image size rejected before any network call, a capped model response length
/// (<c>options.num_predict</c>), and a strict timeout enforced independently of whatever the HTTP
/// client's own defaults are. Any failure along the way -- oversized image, network error,
/// non-success status, malformed/unrecognized JSON, out-of-range confidence, or timeout -- returns a
/// failed/timed-out <see cref="LocalVlmClassification"/> rather than throwing, so a caller on a
/// decision path that must fail open never has to special-case an exception from this adapter.
/// </summary>
public sealed class OllamaLocalVlmClient : ILocalVlmClient
{
    /// <summary>Default cap on the accepted image payload, before any network call is attempted.</summary>
    public const int DefaultMaxImageBytes = 2 * 1024 * 1024;

    /// <summary>Default strict timeout for one classification attempt.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    // Fixed prompt: never built from caller/observed content, so the "bounded prompt" requirement
    // holds regardless of what the image shows. Only the image bytes vary per call.
    private const string Prompt =
        "Classify this TV broadcast frame. Reply with EXACTLY this JSON shape and both fields, nothing else: " +
        "{\"state\": \"Program\" or \"Commercial\" or \"Unknown\", \"confidence\": a number between 0.0 and 1.0}. " +
        "Example: {\"state\": \"Commercial\", \"confidence\": 0.85}. " +
        "Use \"Commercial\" only if you are confident this frame is from an advertisement, " +
        "\"Program\" only if you are confident it is the original broadcast, and \"Unknown\" otherwise. " +
        "Now classify the given image.";

    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly TimeSpan _timeout;
    private readonly int _maxImageBytes;

    public OllamaLocalVlmClient(
        HttpClient httpClient,
        string model = "qwen2.5vl:7b",
        TimeSpan? timeout = null,
        int maxImageBytes = DefaultMaxImageBytes)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        if (timeout is { } t && t <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), t, "Must be positive.");
        }

        if (maxImageBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxImageBytes), maxImageBytes, "Must be positive.");
        }

        _httpClient = httpClient;
        _model = model;
        _timeout = timeout ?? DefaultTimeout;
        _maxImageBytes = maxImageBytes;
    }

    public async Task<LocalVlmClassification> ClassifyAsync(ReadOnlyMemory<byte> imageJpegBytes, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        if (imageJpegBytes.Length > _maxImageBytes)
        {
            return LocalVlmClassification.Failed(
                stopwatch.Elapsed, $"Image size {imageJpegBytes.Length} exceeds the {_maxImageBytes}-byte bound.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        try
        {
            var requestBody = new
            {
                model = _model,
                prompt = Prompt,
                images = new[] { Convert.ToBase64String(imageJpegBytes.Span) },
                format = "json",
                stream = false,
                options = new { num_predict = 64 },
            };

            using var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync("/api/generate", content, timeoutCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return LocalVlmClassification.Failed(stopwatch.Elapsed, $"Ollama returned status {(int)response.StatusCode}.");
            }

            var envelope = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            return ParseEnvelope(envelope, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own timeout fired, not the caller's token -- this is the "strict timeout" case,
            // not a generic failure.
            return LocalVlmClassification.TimedOutResult(stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            return LocalVlmClassification.Failed(stopwatch.Elapsed, ex.Message);
        }
    }

    private static LocalVlmClassification ParseEnvelope(string envelope, TimeSpan latency)
    {
        string responseField;
        try
        {
            using var envelopeDoc = JsonDocument.Parse(envelope);
            if (!envelopeDoc.RootElement.TryGetProperty("response", out var responseProp) ||
                responseProp.ValueKind != JsonValueKind.String)
            {
                return LocalVlmClassification.Failed(latency, "Ollama envelope missing a string 'response' field.");
            }

            responseField = responseProp.GetString()!;
        }
        catch (JsonException ex)
        {
            return LocalVlmClassification.Failed(latency, $"Malformed Ollama envelope: {ex.Message}");
        }

        try
        {
            using var payloadDoc = JsonDocument.Parse(responseField);
            var root = payloadDoc.RootElement;

            if (!root.TryGetProperty("state", out var stateProp) || stateProp.ValueKind != JsonValueKind.String)
            {
                return LocalVlmClassification.Failed(latency, "Model response missing a string 'state' field.");
            }

            if (!Enum.TryParse<BroadcastState>(stateProp.GetString(), ignoreCase: true, out var state))
            {
                return LocalVlmClassification.Failed(latency, $"Unrecognized state value '{stateProp.GetString()}'.");
            }

            if (!root.TryGetProperty("confidence", out var confidenceProp) ||
                confidenceProp.ValueKind != JsonValueKind.Number ||
                !confidenceProp.TryGetDouble(out var confidence))
            {
                return LocalVlmClassification.Failed(latency, "Model response missing a numeric 'confidence' field.");
            }

            if (!double.IsFinite(confidence) || confidence is < 0.0 or > 1.0)
            {
                return LocalVlmClassification.Failed(latency, $"Confidence {confidence} outside [0, 1].");
            }

            return new LocalVlmClassification(state, confidence, latency, TimedOut: false, Error: null);
        }
        catch (JsonException ex)
        {
            return LocalVlmClassification.Failed(latency, $"Malformed model response JSON: {ex.Message}");
        }
    }
}
