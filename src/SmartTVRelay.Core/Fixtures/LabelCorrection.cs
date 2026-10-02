namespace SmartTVRelay.Core.Fixtures;

/// <summary>What a <see cref="LabelCorrection"/> does to the labeled timeline.</summary>
public enum CorrectionOperation
{
    Add,
    Change,
    Remove,
}

/// <summary>
/// One human correction to a labeled <see cref="ExpectedSegment"/> timeline (#25). Corrections edit
/// labels only -- raw media/emissions are never touched, and applying a correction log never
/// re-derives a timeline from a detector. Use <see cref="CreateAdd"/>, <see cref="CreateChange"/>, or
/// <see cref="CreateRemove"/> rather than the constructor directly: they enforce which of
/// <see cref="Target"/>/<see cref="Replacement"/> an operation actually needs, so a correction record
/// can never be built in a shape <see cref="LabelCorrectionApplier"/> wouldn't know how to interpret.
/// </summary>
/// <param name="Id">Stable identity for this correction. <see cref="LabelCorrectionApplier.Apply"/>
/// treats a repeated <see cref="Id"/> within one correction log as the same correction seen twice --
/// applied once, skipped thereafter -- so a correction log can be safely replayed or re-delivered
/// (e.g. by an at-least-once event log) without double-applying anything.</param>
/// <param name="Author">Who made the correction -- provenance, not used by apply logic itself.</param>
/// <param name="Operation">Add, Change, or Remove.</param>
/// <param name="Target">For Change/Remove: the exact segment the correction assumes is currently
/// present. If the timeline no longer contains a segment that matches it exactly (already changed or
/// removed by an earlier correction, or never present), applying this correction is a conflict, not a
/// guess -- see <see cref="LabelCorrectionApplier"/>.</param>
/// <param name="Replacement">For Add/Change: the segment value the correction introduces.</param>
/// <param name="Comment">Free-text human explanation for the correction. Optional, never interpreted
/// by apply logic.</param>
public sealed record LabelCorrection
{
    public Guid Id { get; }
    public string Author { get; }
    public CorrectionOperation Operation { get; }
    public ExpectedSegment? Target { get; }
    public ExpectedSegment? Replacement { get; }
    public string? Comment { get; }

    private LabelCorrection(
        Guid id,
        string author,
        CorrectionOperation operation,
        ExpectedSegment? target,
        ExpectedSegment? replacement,
        string? comment)
    {
        Id = id;
        Author = author;
        Operation = operation;
        Target = target;
        Replacement = replacement;
        Comment = comment;
    }

    public static LabelCorrection CreateAdd(Guid id, string author, ExpectedSegment segment, string? comment = null)
    {
        RequireAuthor(author);
        return new LabelCorrection(id, author, CorrectionOperation.Add, target: null, replacement: segment, comment);
    }

    public static LabelCorrection CreateChange(Guid id, string author, ExpectedSegment target, ExpectedSegment replacement, string? comment = null)
    {
        RequireAuthor(author);
        return new LabelCorrection(id, author, CorrectionOperation.Change, target, replacement, comment);
    }

    public static LabelCorrection CreateRemove(Guid id, string author, ExpectedSegment target, string? comment = null)
    {
        RequireAuthor(author);
        return new LabelCorrection(id, author, CorrectionOperation.Remove, target, replacement: null, comment);
    }

    private static void RequireAuthor(string author)
    {
        if (string.IsNullOrWhiteSpace(author))
        {
            throw new ArgumentException("Author must not be null or whitespace.", nameof(author));
        }
    }
}
