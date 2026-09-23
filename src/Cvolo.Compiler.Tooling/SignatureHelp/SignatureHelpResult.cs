namespace Cvolo.Compiler.Tooling.SignatureHelp;

/// <summary>
/// One parameter in a callable signature exposed to an editor client.
/// </summary>
public sealed record SignatureHelpParameter(string Label);

/// <summary>
/// One callable signature. The label uses Cvolo source spelling and is suitable for direct hover or
/// signature-help presentation.
/// </summary>
public sealed record SignatureHelpItem(
	string Label,
	IReadOnlyList<SignatureHelpParameter> Parameters,
	string? Documentation = null);

/// <summary>
/// Signature-help response for a call at the current cursor position.
/// </summary>
public sealed record SignatureHelpResult(
	IReadOnlyList<SignatureHelpItem> Signatures,
	int ActiveSignature,
	int ActiveParameter);
