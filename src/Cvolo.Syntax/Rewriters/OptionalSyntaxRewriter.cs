using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Syntax.Rewriters;

/// <summary>
/// Lowers the optional type sugar <c>T?</c> into an explicit <c>Option<T></c> before binding:
///   - rewrites the type name in every structural position (declarations, parameters, returns, fields),
///   - synthesizes <c>Option<T> { Some: ... }</c> constructions for plain-value initializers and
///     assignments so an implicit value-to-option conversion becomes explicit syntax,
///   - enforces <c>--strict-option</c> (reports an error for any <c>?</c> use) and rejects
///     <c>null</c> initializers on <c>T?</c>-declared variables (safe-code null is forbidden).
/// Interior occurrences (e.g. <c>Option<int?></c>) and cast/default positions are resolved by
/// <c>BindingContext.ResolveType</c>, which desugars any residual trailing '?' uniformly.
/// </summary>
public sealed class OptionalSyntaxRewriter(bool strictOption, DiagnosticBag diagnostics, CompilationContext fileContext) : AstRewriterBase
{
	private readonly bool _strictOption = strictOption;
	private readonly DiagnosticBag _diagnostics = diagnostics;
	private readonly CompilationContext _fileContext = fileContext;

	/// <summary>
	/// Variable name → inner type for in-scope '?'-declared locals (implicit conversions).
	/// </summary>
	private Dictionary<string, string> _optionLocals = [];

	/// <summary>
	/// Variable name → inner type for '?'-declared globals (implicit conversions).
	/// </summary>
	private readonly Dictionary<string, string> _optionGlobals = [];

	public override SyntaxNode Rewrite(SyntaxNode node)
	{
		// Declaration signature positions: structural '?' desugaring
		if (node is VariableDeclarationSyntax varDecl)
		{
			var rewrittenType = RewriteType(varDecl.Type, varDecl.Span);
			ExpressionSyntax? rewrittenInit = varDecl.Initializer;

			// Bare 'default' in a typed declaration: 'int x = default;' / 'int? x = default;'
			// lower to the declared type, so 'int? x = default;' becomes default(Option<int>) (None).
			if (rewrittenInit is DefaultExpressionSyntax { TypeName: null } bareDefault && rewrittenType is not null)
			{
				rewrittenInit = new DefaultExpressionSyntax(bareDefault.Span, rewrittenType);
			}

			if (IsOptionRoot(varDecl.Type))
			{
				if (rewrittenInit is NullLiteralExpressionSyntax)
				{
					Report(varDecl.Initializer!.Span, DiagnosticIds.NullForOptionalType,
						"null is not allowed in safe code. Use Option.None instead.");
				}
				else if (IsOptionNoneExpression(rewrittenInit))
				{
					rewrittenInit = BuildNone(rewrittenType, rewrittenInit!.Span);
				}
				else if (IsWrapCandidate(rewrittenInit, varDecl.Type[..^1]))
				{
					rewrittenInit = BuildSome(rewrittenInit!, rewrittenType);
				}
			}

			if (rewrittenInit is not null)
				rewrittenInit = (ExpressionSyntax)Rewrite(rewrittenInit);

			TrackLocal(varDecl.Name, varDecl.Type);
			return new VariableDeclarationSyntax(varDecl.Span, varDecl.IsMutable, rewrittenType, varDecl.Name, rewrittenInit);
		}

		if (node is GlobalVariableDeclarationSyntax globalDecl)
		{
			var rewrittenType = RewriteType(globalDecl.Type, globalDecl.Span);
			ExpressionSyntax? rewrittenInit = globalDecl.Initializer;

			// 'global int x = default;' → 'global int x = default(int);' (and 'int?' roots → default(Option<int>))
			if (rewrittenInit is DefaultExpressionSyntax { TypeName: null } bareDefault && rewrittenType is not null)
			{
				rewrittenInit = new DefaultExpressionSyntax(bareDefault.Span, rewrittenType);
			}

			if (IsOptionRoot(globalDecl.Type))
			{
				if (rewrittenInit is NullLiteralExpressionSyntax)
				{
					Report(globalDecl.Initializer!.Span, DiagnosticIds.NullForOptionalType,
						"null is not allowed in safe code. Use Option.None instead.");
				}
				else if (IsOptionNoneExpression(rewrittenInit))
				{
					rewrittenInit = BuildNone(rewrittenType, rewrittenInit!.Span);
				}
				else if (IsWrapCandidate(rewrittenInit, globalDecl.Type[..^1]))
				{
					rewrittenInit = BuildSome(rewrittenInit!, rewrittenType);
				}
			}

			if (rewrittenInit is not null)
				rewrittenInit = (ExpressionSyntax)Rewrite(rewrittenInit);

			TrackLocal(globalDecl.Name, globalDecl.Type);
			return new GlobalVariableDeclarationSyntax(globalDecl.Span, rewrittenType, globalDecl.Name, rewrittenInit, globalDecl.IsMutable, globalDecl.Visibility);
		}

		if (node is ParameterSyntax param)
		{
			return new ParameterSyntax(param.Span, RewriteType(param.Type, param.Span), param.Name, param.Attributes);
		}

		if (node is FunctionDeclarationSyntax func)
		{
			var rewrittenParams = func.Parameters.Select(p => (ParameterSyntax)Rewrite(p)).ToList();
			var rewrittenBody = func.Body != null ? (BlockStatementSyntax)Rewrite(func.Body) : null;
			return new FunctionDeclarationSyntax(func.Span, RewriteType(func.ReturnType, func.Span), func.Name, func.GenericParameters,
				rewrittenParams, rewrittenBody!, func.Attributes, func.Modifier, func.Receiver, func.Visibility);
		}

		if (node is ConstructorDeclarationSyntax ctor)
		{
			var rewrittenParams = ctor.Parameters.Select(p => (ParameterSyntax)Rewrite(p)).ToList();
			var rewrittenBody = (BlockStatementSyntax)Rewrite(ctor.Body);
			return new ConstructorDeclarationSyntax(ctor.Span, ctor.StructName, rewrittenParams, rewrittenBody,
				ctor.ConstructorArguments, ctor.ConstructorInitializerSpan, ctor.Attributes, ctor.Visibility);
		}

		if (node is StructDeclarationSyntax structDecl)
		{
			var rewrittenFields = structDecl.Fields.Select(f => new StructFieldSyntax(f.Span, RewriteType(f.Type, f.Span), f.Name, f.Visibility)).ToList();
			return new StructDeclarationSyntax(structDecl.Span, structDecl.Name, structDecl.GenericParameters, rewrittenFields,
				structDecl.EmbeddedType, structDecl.Attributes, structDecl.Visibility, structDecl.GenericParameterDefaults, structDecl.GenericParameterConstraints);
		}

		if (node is UnionFieldSyntax unionField)
		{
			return new UnionFieldSyntax(unionField.Span, RewriteType(unionField.Type, unionField.Span), unionField.Name, unionField.Visibility);
		}

		if (node is ExtensionDeclarationSyntax extDecl)
		{
			var rewrittenMethods = extDecl.Methods.Select(m => (FunctionDeclarationSyntax)Rewrite(m)).ToList();
			var rewrittenDtors = extDecl.Destructors.Select(d => (DestructorDeclarationSyntax)Rewrite(d)).ToList();
			var rewrittenCtors = extDecl.Constructors.Select(c => (ConstructorDeclarationSyntax)Rewrite(c)).ToList();
			return new ExtensionDeclarationSyntax(extDecl.Span, RewriteType(extDecl.ExtendedTypeName, extDecl.Span), rewrittenMethods,
				rewrittenDtors, rewrittenCtors, extDecl.GenericParameters, extDecl.ConformsTo, extDecl.Visibility, extDecl.GenericParameterDefaults);
		}

		if (node is InterfaceMethodDeclarationSyntax interfaceMember)
		{
			var rewrittenParams = interfaceMember.Parameters.Select(p => (ParameterSyntax)Rewrite(p)).ToList();
			return new InterfaceMethodDeclarationSyntax(interfaceMember.Span, RewriteType(interfaceMember.ReturnType, interfaceMember.Span), interfaceMember.Name, rewrittenParams);
		}

		if (node is ProtocolMethodDeclarationSyntax protocolMember)
		{
			var rewrittenParams = protocolMember.Parameters.Select(p => (ParameterSyntax)Rewrite(p)).ToList();
			return new ProtocolMethodDeclarationSyntax(protocolMember.Span, RewriteType(protocolMember.ReturnType, protocolMember.Span), protocolMember.Name, rewrittenParams);
		}

		// Implicit conversion on plain assignments:  x = <value>  →  x = Option<T> { Some: <value> }
		if (node is BinaryExpressionSyntax bin && bin.Operator == "=" && bin.Left is IdentifierExpressionSyntax id)
		{
			string? inner = null;
			if (_optionLocals.TryGetValue(id.Name, out var localInner))
			{
				inner = localInner;
			}
			else if (_optionGlobals.TryGetValue(id.Name, out var globalInner))
			{
				inner = globalInner;
			}

			if (inner is not null)
			{
				if (bin.Right is NullLiteralExpressionSyntax)
				{
					Report(bin.Right.Span, DiagnosticIds.NullForOptionalType,
						"null is not allowed in safe code. Use Option.None instead.");
				}
				else if (IsOptionNoneExpression(bin.Right))
				{
					var rewrittenNone = BuildNone($"Option<{inner}>", bin.Right.Span);
					return new BinaryExpressionSyntax(bin.Span, bin.Left, "=", rewrittenNone);
				}
				else if (IsWrapCandidate(bin.Right, inner))
				{
					var rewrittenRight = (ExpressionSyntax)Rewrite(bin.Right);
					return new BinaryExpressionSyntax(bin.Span, bin.Left, "=", BuildSome(rewrittenRight, $"Option<{inner}>"));
				}
			}

			return base.Rewrite(node);
		}

		// Block scope tracking so shadowed locals restore the outer '?' mapping
		if (node is BlockStatementSyntax block)
		{
			var snapshot = _optionLocals;
			_optionLocals = new Dictionary<string, string>(snapshot);
			var rewritten = (BlockStatementSyntax)base.Rewrite(block);
			_optionLocals = snapshot;
			return rewritten;
		}

		return base.Rewrite(node);
	}

	/// <summary>
	/// Rewrites a structural type string, desugaring a trailing '?' into an explicit Option<T>.
	/// Value types (and refs) become a '?'-wrapped option: 'int?' → 'Option<int>', and a reference
	/// root becomes a flat NPO option: 'ref Node?' → 'Option<ref Node>' ('refvar' likewise).
	/// 'void?' is left untouched so the resolver reports it (CVL1102).
	/// </summary>
	private string RewriteType(string? type, TextSpan reportSpan)
	{
		if (string.IsNullOrEmpty(type) || !type.EndsWith('?'))
			return type!;

		if (_strictOption)
		{
			Report(reportSpan, DiagnosticIds.OptionalSyntaxDisabled,
				"Optional syntax '?' is disabled. Use explicit Option<T> type.");
		}

		var inner = type[..^1];
		if (inner == "void")
			return type;

		if (inner.StartsWith("ref ", StringComparison.Ordinal) || inner.StartsWith("refvar ", StringComparison.Ordinal))
			return "Option<" + inner + ">";

		return "Option<" + RewriteType(inner, reportSpan) + ">";
	}

	/// <summary>
	/// True when '?' applies to the outermost type (a true T? root). Reference roots
	/// ('ref T?', 'refvar T?') are included - they lower to the flat NPO 'Option<ref T>'.
	/// </summary>
	private static bool IsOptionRoot(string? type) =>
		type is not null && type.EndsWith('?') && type.Length > 1;

	/// <summary>
	/// True when the RHS is the `Option.None` none-state initializer.
	/// </summary>
	private static bool IsOptionNoneExpression(ExpressionSyntax? expr)
		=> expr is MemberAccessExpressionSyntax m &&
		m.MemberName == "None" &&
		m.Expression is IdentifierExpressionSyntax id && id.Name == "Option";

	/// <summary>
	/// Builds 'Option<T>; { None: void }' as an explicit none-state construction.
	/// </summary>
	private static StructInitializationExpressionSyntax BuildNone(string optionTypeName, TextSpan span)
		=> new(span, optionTypeName, [new MemberInitializerSyntax(span, "None", new VoidLiteralExpressionSyntax(span))]);

	/// <summary>
	/// True when an RHS can be wrapped in 'Option<T> { Some: ... }' without risk of double
	/// wrapping. For '?'-declared reference roots ('ref T?', 'refvar T?') a borrow expression is also
	/// eligible — it can never be an option itself.
	/// </summary>
	private static bool IsWrapCandidate(ExpressionSyntax? expr, string inner)
		=> IsPlainValue(expr) || ((inner.StartsWith("ref ", StringComparison.Ordinal) || inner.StartsWith("refvar ", StringComparison.Ordinal))
			&& expr is BorrowExpressionSyntax);

	/// <summary>
	/// Syntactic heuristic for RHS expressions that can never be an Option themselves, so wrapping
	/// them in 'Option<T> { Some: ... }' can never double-wrap: literals, struct initializers and
	/// default(...). Identifiers, calls, member accesses etc. are left for the binder to check.
	/// </summary>
	private static bool IsPlainValue(ExpressionSyntax? expr) => expr switch
	{
		IntegerLiteralExpressionSyntax or DoubleLiteralExpressionSyntax or StringLiteralExpressionSyntax
			or CharacterLiteralExpressionSyntax or BooleanLiteralExpressionSyntax
			or ArrayInitializationExpressionSyntax or ArrayReplicationExpressionSyntax => true,
		StructInitializationExpressionSyntax si => !IsExplicitOptionType(si.StructTypeName),
		DefaultExpressionSyntax d => d.TypeName is not null && !IsExplicitOptionType(d.TypeName) && !d.TypeName.EndsWith('?'),
		_ => false,
	};

	/// <summary>
	/// True when a type name refers to an explicit Option<...> (i.e. the value is already an option and must not be wrapped again inside a 'T?' local).
	/// </summary>
	private static bool IsExplicitOptionType(string? typeName)
		=> typeName == "Option" || (typeName?.StartsWith("Option<", StringComparison.Ordinal) ?? false);

	/// <summary>Builds 'Option<T> { Some: value }' as an explicit tagged-union construction.</summary>
	private static StructInitializationExpressionSyntax BuildSome(ExpressionSyntax value, string optionTypeName) =>
		new(value.Span, optionTypeName, [new MemberInitializerSyntax(value.Span, "Some", value)]);

	private void TrackLocal(string name, string? originalType)
	{
		if (IsOptionRoot(originalType) && !originalType![..^1].Equals("void", StringComparison.Ordinal))
		{
			var inner = originalType[..^1];
			_optionLocals[name] = inner;
		}
		else if (_optionLocals.ContainsKey(name))
		{
			_optionLocals.Remove(name);
		}
	}

	private void Report(TextSpan span, string diagnosticId, string message)
	{
		_diagnostics.Report(_fileContext, span, message, diagnosticId);
	}
}
