using Cvolo.Analysis.Symbols;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Declaration;

/// <summary>
/// Validates compiler-recognized attributes and applies their declaration-time semantic effects.
/// </summary>
/// <remarks>
/// Attribute syntax is erased before LLVM emission. This service centralizes declaration-time
/// target/context validation, warning suppression, function-symbol flags, and the specialized
/// payload parsing used by FFI/export attributes without changing declaration-pass ordering.
/// </remarks>
internal sealed class AttributeValidator(BindingContext context)
{
	// M1 attribute model: only System.* intrinsics exist. Their [AttributeUsage]-style rules
	// (syntactic target x safety context, per spec section 4) are modeled compiler-side until
	// the language has enums/inheritance to declare them in source.
	private static readonly Dictionary<string, (string[] Targets, SafetyTier[] Contexts)> IntrinsicAttributes = new()
	{
		["UnsafeBody"] = (["Function", "Method", "Constructor", "Destructor"], [SafetyTier.Safe, SafetyTier.Unbound, SafetyTier.Unsafe]),
		["NoAlias"] = (["Function", "Method", "Parameter"], [SafetyTier.Unbound, SafetyTier.Unsafe]),
		["SuppressWarning"] = (["Struct", "Function", "Method", "Constructor", "Destructor", "Parameter"], [SafetyTier.Safe, SafetyTier.Unbound, SafetyTier.Unsafe]),
		["Flags"] = (["Struct"], [SafetyTier.Safe, SafetyTier.Unbound, SafetyTier.Unsafe]),
		["NonExhaustive"] = (["Struct"], [SafetyTier.Safe, SafetyTier.Unbound, SafetyTier.Unsafe]),
		["StrictMutability"] = (["Struct"], [SafetyTier.Safe, SafetyTier.Unbound, SafetyTier.Unsafe]),
		["Intrinsic"] = (["Function", "Method"], [SafetyTier.Safe, SafetyTier.Unbound, SafetyTier.Unsafe]),
		["MustUse"] = (["Function", "Method", "Constructor", "Struct", "Union", "Enum"], [SafetyTier.Safe, SafetyTier.Unbound, SafetyTier.Unsafe]),
		["Error"] = (["Struct", "Union", "Enum"], [SafetyTier.Safe, SafetyTier.Unbound, SafetyTier.Unsafe]),
		["Inline"] = (["Function", "Method", "Constructor"], [SafetyTier.Safe, SafetyTier.Unbound, SafetyTier.Unsafe]),
		["NeverInline"] = (["Function", "Method", "Constructor"], [SafetyTier.Safe, SafetyTier.Unbound, SafetyTier.Unsafe]),
		["LibraryImport"] = (["ExternBlock"], [SafetyTier.Safe, SafetyTier.Unbound, SafetyTier.Unsafe]),
		["ImportName"] = (["ExternBlockFunction"], [SafetyTier.Safe, SafetyTier.Unbound, SafetyTier.Unsafe]),
		["ExposeName"] = (["Function"], [SafetyTier.Safe, SafetyTier.Unbound, SafetyTier.Unsafe]),
	};

	private static readonly HashSet<string> KnownWarningIds =
	[
		DiagnosticIds.UnsafeBodyNoEffect,
		DiagnosticIds.UnknownAttribute,
		DiagnosticIds.UnboundNoRefParams,
		DiagnosticIds.AutoInferMutationWarning,
		DiagnosticIds.MustUseIgnoredWarning,
		DiagnosticIds.InlineOnRecursiveFunction,
		DiagnosticIds.LargeCopyWarning
	];

	/// <summary>
	/// Returns the canonical compiler-recognized attribute names in declaration order.
	/// </summary>
	public static IReadOnlyCollection<string> IntrinsicAttributeNames => IntrinsicAttributes.Keys;

	/// <summary>
	/// Normalizes a possibly qualified attribute name and removes the optional <c>Attribute</c>
	/// suffix, returning null when the resulting name is not compiler-recognized.
	/// </summary>
	public string? NormalizeName(string attributeName)
	{
		var simple = attributeName.Contains('.') ? attributeName[(attributeName.LastIndexOf('.') + 1)..] : attributeName;
		if (simple.EndsWith("Attribute", StringComparison.Ordinal))
			simple = simple[..^"Attribute".Length];

		return IntrinsicAttributes.ContainsKey(simple) ? simple : null;
	}

	/// <summary>
	/// Verifies attributes against their syntactic target and safety context and returns the
	/// canonical keys whose applications are valid.
	/// </summary>
	public List<string> Verify(
		IReadOnlyList<AttributeSyntax> attributes,
		string syntacticTarget,
		List<string>? suppressedWarnings = null,
		SafetyTier safetyTier = SafetyTier.Safe)
	{
		var applied = new List<string>();
		var seen = new HashSet<string>();
		var unknownAttributes = new List<AttributeSyntax>();
		foreach (var attr in attributes)
		{
			var key = NormalizeName(attr.Name);
			if (key is null)
			{
				// Preserve the hybrid stance: unknown attributes are erased at codegen but warned
				// after the full list is scanned so SuppressWarning works regardless of order.
				unknownAttributes.Add(attr);
				continue;
			}

			if (key == "LibraryImport")
			{
				ReportDiagnostic(attr, "Attribute '[LibraryImport]' can only be applied to an extern block.", DiagnosticIds.LibraryImportOnNonBlock);
				continue;
			}

			if (key == "ImportName")
			{
				ReportDiagnostic(attr, "Attribute '[ImportName]' can only be applied to a function declaration inside an extern block.", DiagnosticIds.ImportNameOutsideBlock);
				continue;
			}

			if (key == "ExposeName")
			{
				ReportDiagnostic(attr, "[ExposeName] can only be applied to functions marked for binary export via the `expose` modifier.", DiagnosticIds.ExposeNameOutsideExport);
				continue;
			}

			if (!seen.Add(key))
			{
				ReportDiagnostic(attr, $"Duplicate attribute '[{key}]'.");
				continue;
			}

			var (targets, contexts) = IntrinsicAttributes[key];
			if (!targets.Contains(syntacticTarget))
			{
				ReportDiagnostic(attr, $"Attribute '[{key}]' cannot be applied to {syntacticTarget.ToLowerInvariant()} declarations.");
				continue;
			}

			if (!contexts.Contains(safetyTier))
			{
				ReportDiagnostic(attr, $"Attribute '[{key}]' cannot be applied in {safetyTier} context.");
				continue;
			}

			if (key == "SuppressWarning")
			{
				ApplySuppressWarning(attr, suppressedWarnings);
				continue;
			}

			applied.Add(key);
		}

		foreach (var unknown in unknownAttributes)
		{
			if (suppressedWarnings?.Contains(DiagnosticIds.UnknownAttribute) == true)
				continue;

			ReportWarning(unknown, $"Unknown attribute '{unknown.Name}'; it will be ignored.", DiagnosticIds.UnknownAttribute);
		}

		return applied;
	}

	/// <summary>
	/// Applies already-validated function attributes to the resolved function symbol and copies
	/// declaration-level warning suppressions onto that symbol.
	/// </summary>
	public void ApplyFunctionAttributes(
		List<string> appliedKeys,
		FunctionSymbol symbol,
		List<string> suppressedWarnings,
		IReadOnlyList<AttributeSyntax>? attributes = null)
	{
		if (appliedKeys.Contains("UnsafeBody"))
			symbol.IsUnsafeBody = true;

		if (appliedKeys.Contains("NoAlias"))
			symbol.IsNoAlias = true;

		if (appliedKeys.Contains("MustUse") && attributes != null)
		{
			var (isMustUse, mustUseMsg) = ExtractMustUse(attributes);
			symbol.IsMustUse = isMustUse;
			symbol.MustUseMessage = mustUseMsg;
		}

		if (appliedKeys.Contains("Intrinsic") && attributes is not null)
		{
			var intrinsicAttr = attributes.FirstOrDefault(a => a.Name is "Intrinsic" or "System.Intrinsic" or "IntrinsicAttribute");
			if (intrinsicAttr?.Arguments.Count > 0 && intrinsicAttr.Arguments[0] is StringLiteralExpressionSyntax str)
				symbol.IntrinsicName = str.Value;
		}

		var inline = appliedKeys.Contains("Inline");
		var neverInline = appliedKeys.Contains("NeverInline");
		if (inline && neverInline)
		{
			var conflicting = attributes?.FirstOrDefault(a =>
			{
				var normalized = NormalizeName(a.Name);
				return normalized is "Inline" or "NeverInline";
			});
			if (conflicting is not null)
			{
				ReportDiagnostic(
					conflicting,
					$"Attribute '[{NormalizeName(conflicting.Name)}]' cannot be combined with the other inlining attribute on the same declaration.",
					DiagnosticIds.ConflictingInlineAttributes);
			}
		}

		if (inline)
			symbol.IsInline = true;
		if (neverInline)
			symbol.IsNeverInline = true;

		foreach (var warningId in suppressedWarnings)
			symbol.SuppressedWarnings.Add(warningId);
	}

	/// <summary>
	/// Reports the existing suppressible warning when <c>[UnsafeBody]</c> is present but the body
	/// contains no operation classified as unsafe.
	/// </summary>
	public void WarnIfUnsafeBodyUnused(
		TextSpan declarationSpan,
		SyntaxNode body,
		FunctionSymbol symbol,
		List<string> suppressedWarnings)
	{
		if (body is null)
			return;
		if (!symbol.IsUnsafeBody || suppressedWarnings.Contains(DiagnosticIds.UnsafeBodyNoEffect))
			return;
		if (UnsafeOperationScanner.ContainsUnsafeOperations(body))
			return;

		context.Diagnostics.ReportWarning(
			context.FileContexts[context.CurrentUnit!],
			declarationSpan,
			"'[UnsafeBody]' attribute has no effect because function contains no unsafe operations.",
			DiagnosticIds.UnsafeBodyNoEffect);
	}

	/// <summary>
	/// Reports the existing advisory warning when an inline-marked function directly references
	/// itself and the warning is not suppressed.
	/// </summary>
	public void WarnIfInlineRecursive(
		FunctionDeclarationSyntax function,
		FunctionSymbol symbol,
		IReadOnlyCollection<string> suppressedWarnings)
	{
		if (!symbol.IsInline
			|| !function.HasBody
			|| suppressedWarnings.Contains(DiagnosticIds.InlineOnRecursiveFunction)
			|| !BodyReferencesFunction(function.Body!, function.Name))
		{
			return;
		}

		ReportWarning(
			function,
			$"Function '{function.Name}' is recursive; LLVM may ignore the '[Inline]' hint.",
			DiagnosticIds.InlineOnRecursiveFunction);
	}

	/// <summary>
	/// Extracts the binary export symbol override from a validated <c>[ExposeName]</c> attribute.
	/// </summary>
	public string? ExtractExposeName(AttributeSyntax attr)
	{
		if (attr.Arguments.Count == 1 && attr.Arguments[0] is StringLiteralExpressionSyntax literal)
			return literal.Value;

		ReportDiagnostic(attr, "Attribute '[ExposeName]' requires exactly one string literal argument naming the exported symbol.");
		return null;
	}

	/// <summary>
	/// Applies one <c>[LibraryImport]</c> attribute to the accumulated native library name and
	/// platform-specific path overrides.
	/// </summary>
	public (string? LibraryName, string? WinPath, string? LinuxPath, string? MacPath) ExtractLibraryImport(
		AttributeSyntax attr,
		string? libraryName,
		string? winPath,
		string? linuxPath,
		string? macPath)
	{
		for (var i = 0; i < attr.Arguments.Count; i++)
		{
			var argName = attr.ArgumentNames.Count > i ? attr.ArgumentNames[i] : null;
			var expr = attr.Arguments[i];
			if (expr is not StringLiteralExpressionSyntax lit)
			{
				ReportDiagnostic(attr, "Attribute '[LibraryImport]' arguments must be string literals (the library name, then optional 'win'/'linux'/'mac' native paths).");
				continue;
			}

			if (argName is null)
			{
				if (libraryName is not null)
				{
					ReportDiagnostic(attr, "Attribute '[LibraryImport]' accepts at most one positional argument: the library name.");
					continue;
				}

				libraryName = lit.Value;
			}
			else
			{
				switch (argName)
				{
					case "win":
						winPath = lit.Value;
						break;
					case "linux":
						linuxPath = lit.Value;
						break;
					case "mac":
						macPath = lit.Value;
						break;
					default:
						ReportDiagnostic(attr, $"Unknown [LibraryImport] named argument '{argName}'. Supported names are 'win', 'linux' and 'mac'.");
						break;
				}
			}
		}

		return (libraryName, winPath, linuxPath, macPath);
	}

	/// <summary>
	/// Extracts the native symbol override from a validated <c>[ImportName]</c> attribute.
	/// </summary>
	public string? ExtractImportName(AttributeSyntax attr)
	{
		if (attr.Arguments.Count != 1 || attr.Arguments[0] is not StringLiteralExpressionSyntax literal)
		{
			ReportDiagnostic(attr, "Attribute '[ImportName]' requires exactly one string literal argument naming the native symbol.");
			return null;
		}

		return literal.Value;
	}

	/// <summary>
	/// Extracts the optional message carried by a <c>[MustUse]</c> attribute while preserving the
	/// existing diagnostic for invalid argument shapes.
	/// </summary>
	public (bool IsMustUse, string? Message) ExtractMustUse(IReadOnlyList<AttributeSyntax> attributes)
	{
		foreach (var attr in attributes)
		{
			if (NormalizeName(attr.Name) != "MustUse")
				continue;

			if (attr.Arguments.Count == 0)
				return (true, null);
			if (attr.Arguments.Count == 1 && attr.Arguments[0] is StringLiteralExpressionSyntax strLit)
				return (true, strLit.Value);

			ReportDiagnostic(attr, "Attribute '[MustUse]' expects at most one string literal argument.");
			return (true, null);
		}

		return (false, null);
	}

	/// <summary>
	/// Validates one <c>[SuppressWarning]</c> payload and records the warning id when recognized.
	/// </summary>
	private void ApplySuppressWarning(AttributeSyntax attr, List<string>? suppressedWarnings)
	{
		if (attr.Arguments.Count != 1 || attr.Arguments[0] is not StringLiteralExpressionSyntax literal)
		{
			ReportDiagnostic(attr, "Attribute '[SuppressWarning]' requires exactly one string literal argument.");
			return;
		}

		var warningId = literal.Value;
		if (!KnownWarningIds.Contains(warningId))
		{
			ReportDiagnostic(attr, $"Unknown warning id '{warningId}'.");
			return;
		}

		suppressedWarnings?.Add(warningId);
	}

	/// <summary>
	/// Returns true when the syntax tree contains a direct call whose simple function name matches
	/// the supplied declaration name.
	/// </summary>
	private static bool BodyReferencesFunction(SyntaxNode node, string functionName)
	{
		if (node is CallExpressionSyntax call && call.FunctionName == functionName)
			return true;

		foreach (var child in node.GetChildren())
		{
			if (BodyReferencesFunction(child, functionName))
				return true;
		}

		return false;
	}

	/// <summary>
	/// Reports an attribute diagnostic without a dedicated diagnostic id in the current source file.
	/// </summary>
	private void ReportDiagnostic(SyntaxNode node, string message)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.Report(currentFileContext, node.Span, message);
	}

	/// <summary>
	/// Reports an attribute diagnostic with the supplied diagnostic id in the current source file.
	/// </summary>
	private void ReportDiagnostic(SyntaxNode node, string message, string diagnosticId)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.Report(currentFileContext, node.Span, message, diagnosticId);
	}

	/// <summary>
	/// Reports an attribute warning with the supplied diagnostic id in the current source file.
	/// </summary>
	private void ReportWarning(SyntaxNode node, string message, string diagnosticId)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.ReportWarning(currentFileContext, node.Span, message, diagnosticId);
	}
}
