namespace SmartTVRelay.Core.Vlm;

/// <summary>One already-computed local-VLM classification (#43), paired with when the source frame was captured.</summary>
public sealed record LocalVlmSample(LocalVlmClassification Classification, DateTimeOffset CapturedAt);
