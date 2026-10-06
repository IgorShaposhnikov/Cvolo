using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;

namespace Cvolo.Analysis.Formatting;

/// <summary>
/// The already-bound canonical formatting operation for one value, keyed by its call syntax in
/// <see cref="BindingContext.ResolvedFormattingOperations"/>. The code generator consumes this
/// descriptor and never re-runs conformance checks or member selection.
/// </summary>
public sealed record ResolvedFormattingOperation
{
	/// <summary>\n\n
	/// The semantic type of the formatted value.
	/// </summary>
	public required TypeSymbol ValueType { get; init; }
	/// <summary>
	/// Which canonical operation was bound.
	/// </summary>
	public required FormattingOperationKind Kind { get; init; }
	/// <summary>
	/// The concrete conformance member invoked for <see cref="FormattingOperationKind.IFormattable"/>
	/// (<c>Format(refvar Formatter)</c>) or <see cref="FormattingOperationKind.IStringable"/>
	/// (<c>ToString()</c>). Its <see cref="FunctionSymbol.Name"/> is the module emission key.
	/// </summary>
	public FunctionSymbol? TargetMethod { get; init; }
	/// <summary>
	/// Bound payload operation for the canonical <c>Option</c> composite (<c>Some</c> value).
	/// </summary>
	public ResolvedFormattingOperation? Payload { get; init; }
	/// <summary>
	/// Bound payload operation for the canonical <c>Result</c> composite <c>Ok</c> value.
	/// </summary>
	public ResolvedFormattingOperation? SuccessPayload { get; init; }
	/// <summary>
	/// Bound payload operation for the canonical <c>Result</c> composite <c>Err</c> value.
	/// </summary>
	public ResolvedFormattingOperation? ErrorPayload { get; init; }
	/// <summary>
	/// Composite open spelling (<c>"Some("</c> / <c>"Ok("</c>), written before the payload.
	/// </summary>
	public string? OpenText { get; init; }
	/// <summary>
	/// Composite alternate open spelling (<c>"Err("</c>), written before the error payload.
	/// </summary>
	public string? AlternateOpenText { get; init; }
	/// <summary>
	/// Composite close spelling (<c>")"</c>), written after the payload.
	/// </summary>
	public string? CloseText { get; init; }
	/// <summary>
	/// Composite empty spelling (<c>"None"</c>) written when the alternate payload is absent.
	/// </summary>
	public string? AlternateText { get; init; }
	/// <summary>
	/// The call shape that directed this operation (controls emitter receiver construction).
	/// </summary>
	public required FormattingContextKind Context { get; init; }
}
