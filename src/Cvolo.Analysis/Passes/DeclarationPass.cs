using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Analysis.Passes.Declaration;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Directives;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes;

public sealed class DeclarationPass(BindingContext context)
{
	private readonly TypeAliasValidator _aliases = new(context);
	private readonly TypeDeclarationRegistrar _types = new(context);
	private readonly ContractHierarchyLinker _contracts = new(context);
	private readonly EmbedLinker _embeds = new(context);
	private readonly EmbeddedMethodPromoter _embeddedMethods = new(context);
	private readonly DestructorValidator _destructors = new(context);
	private readonly GenericDefaultConstraintValidator _genericDefaults = new(context);
	private readonly GenericDefaultCopyValidator _genericDefaultCopies = new(context);
	private readonly FunctionDeclarationRegistrar _functions = new(context);
	private ExtensionRegistrar? _extensions;
	private ExtensionRegistrar Extensions => _extensions ??= new(context, _functions, _destructors, _genericDefaultCopies);
	/// <summary>
	/// The compiler's built-in attribute names, without the optional <c>Attribute</c> suffix, in
	/// declaration order. Tooling surfaces use this forwarding property to preserve the existing
	/// <see cref="DeclarationPass"/> API while attribute semantics live in <see cref="AttributeValidator"/>.
	/// </summary>
	public static IReadOnlyCollection<string> IntrinsicAttributeNames => AttributeValidator.IntrinsicAttributeNames;



	public void Process(IEnumerable<CompilationUnitSyntax> units)
	{
		ProcessExposeUsings(units);

		// Pass 0a-pre: Register all type aliases before any type/field/parameter resolution
		// so struct fields and function signatures may reference them. Names are reserved
		// now; underlying types are validated eagerly in Pass 0a-post (below).
		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			context.CurrentNamespace = unit.NamespaceDeclaration?.Name;

			var aliasMembers = context.CurrentNamespace != null ? unit.NamespaceDeclaration!.Members : unit.Members;
			foreach (var member in aliasMembers)
			{
				if (member is TypeAliasDeclarationSyntax aliasDecl)
					_aliases.Declare(aliasDecl);
			}
		}

		// Pass 0a: Register all Struct/Union/Interface/Protocol raw symbols across all files
		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			context.CurrentNamespace = unit.NamespaceDeclaration?.Name;

			var members = context.CurrentNamespace != null ? unit.NamespaceDeclaration!.Members : unit.Members;
			foreach (var member in members)
			{
				if (member is StructDeclarationSyntax structDecl)
					_types.DeclareStruct(structDecl);
				else if (member is UnionDeclarationSyntax unionDecl)
					_types.DeclareUnion(unionDecl);
				else if (member is InterfaceDeclarationSyntax interfaceDecl)
					_types.DeclareInterface(interfaceDecl);
				else if (member is ProtocolDeclarationSyntax protocolDecl)
					_types.DeclareProtocol(protocolDecl);
				else if (member is EnumDeclarationSyntax enumDecl)
					_types.DeclareEnum(enumDecl);
				else if (member is DelegateDeclarationSyntax delegateDecl)
					_types.DeclareDelegate(delegateDecl);
			}
		}

		// Pass 0a-post: Eagerly validate every type alias now that all real type names are
		// registered — alias-vs-type conflicts, unresolvable targets (CVL1200), alias cycles
		// (CVL1201), and aliases in `where` constraints (CVL1202).
		_aliases.Validate(units);

		// Pass 0b: Link contract hierarchy (`:` base clauses) after every raw contract symbol exists.
		_contracts.Link(units);

		// Pass 0c: Link `struct T embed Base` clauses — validate the embedded type,
		// detect cycles/generics, and rebuild struct symbols with the embedded
		// fields flattened at the FRONT of their layout.
		_embeds.Link(units);

		// Pass 1: Register all Function/Extern signatures across all files
		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			context.CurrentNamespace = unit.NamespaceDeclaration?.Name;

			var members = unit.NamespaceDeclaration != null ? unit.NamespaceDeclaration.Members : unit.Members;
			foreach (var member in members)
			{
				if (member is FunctionDeclarationSyntax func)
					_functions.DeclareFunction(func);
				else if (member is ExternDeclarationSyntax ext)
					_functions.DeclareExternFunction(ext);
				else if (member is ExternBlockSyntax extBlock)
					_functions.DeclareExternBlock(extBlock);
				else if (member is ExposeExternBlockSyntax exportBlock)
					_functions.DeclareExposeExternBlock(exportBlock);
				else if (member is ExtensionDeclarationSyntax extDecl)
					Extensions.Declare(extDecl);
				else if (member is GlobalVariableDeclarationSyntax globalDecl)
					DeclareGlobalVariable(globalDecl);
			}
		}

		// Pass 1.5: Promote embedded-type extension methods onto every struct that
		// embeds them — `w.TakeDamage(20)` on a struct that `embed`s BaseEntity
		// resolves BaseEntity's extension with the outer struct as `this`.
		_embeddedMethods.Promote(units);

		// Pass 2: Enforce the destructor nesting-depth limit (Memory & Safety §2). Run after every
		// struct symbol (embeds included) is fully materialized so the transitive ownership graph
		// is complete.
		_destructors.ValidateDepth(units);

		// Pass 2.5: Validate that default generic parameter types satisfy their `where X : T`
		// constraints (CVL1042). This must run AFTER Pass 1 registers extension methods —
		// structural protocol conformance is proven by the presence of the extended methods
		// on the concrete type — so it cannot live inside Pass 0a's DeclareStruct.
		_genericDefaults.Validate(units);
	}


	private void DeclareGlobalVariable(GlobalVariableDeclarationSyntax globalDecl)
	{
		// Reject 'global var ref/refvar ...' — reference types use ref/refvar directly, not var
		if (globalDecl.IsMutable && globalDecl.Type is not null && globalDecl.Type.StartsWith("ref"))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span,
				$"Cannot use 'var' with reference type in global declaration. Use 'global ref' or 'global refvar' instead.");
			return;
		}

		var type = context.ResolveType(globalDecl.Type);
		if (type is null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span, $"Unknown type '{globalDecl.Type}' in global variable '{globalDecl.Name}'.");
			return;
		}

		var qualifiedName = context.GetMangledName(globalDecl.Name, context.CurrentNamespace);
		if (context.GlobalsByQualifiedName.ContainsKey(qualifiedName))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span, $"Duplicate definition of global variable '{globalDecl.Name}'.");
			return;
		}

		if (ContainsConstantIntegerDivisionByZero(globalDecl.Initializer))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span, $"Division by zero in constant initializer for global variable '{globalDecl.Name}'.", DiagnosticIds.ConstantIntegerDivisionByZero);
			return;
		}

		// §16: safe delegates are non-null / non-default-initializable; a delegate-typed
		// global must carry an explicit initializer, and 'null' is never a legal value.
		if (type is DelegateTypeSymbol)
		{
			if (globalDecl.Initializer is null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, globalDecl.Span,
					$"Global delegate '{globalDecl.Name}' requires an initializer; delegates are non-null and cannot be default-initialized.");
				return;
			}

			if (globalDecl.Initializer is NullLiteralExpressionSyntax)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, globalDecl.Initializer.Span,
					$"Cannot initialize delegate '{globalDecl.Name}' with 'null'; delegates are non-null values.",
					DiagnosticIds.NullLiteralForDelegate);
				return;
			}
		}

		// Globals may reference a function group when the slot is delegate-typed; the actual
		// conversion is resolved and recorded during validation for the emitter.
		if (type is DelegateTypeSymbol && globalDecl.Initializer is IdentifierExpressionSyntax)
		{
			// Fall through: function references are treated as usable global initializers.
		}
		else if (!IsCompileTimeConstant(globalDecl.Initializer))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span, $"Global variable '{globalDecl.Name}' must be initialized with a compile-time constant.", DiagnosticIds.GlobalInitializerNotConstant);
			return;
		}

		// CVL1036 (§TBAA): a multi-word container exposed with module-wide (ABI) visibility
		// invites unsynchronized 16-byte register tearing. Single-word public scalars and small
		// (<9 byte) aggregates are fine; anything wider must live behind a Lock/Mutex wrapper.
		if (!context.LegacyVisibility && globalDecl.Visibility == Visibility.Public && IsMultiWordContainer(type))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span,
				$"Shared multi-word container '{globalDecl.Name}' cannot be exposed publicly without synchronization. Potential 16-byte register tearing and Type Confusion detected. Wrap the global in a 'Lock' or 'Mutex'.",
				DiagnosticIds.MultiWordPublicGlobal);
		}

		// ref/refvar globals are inherently re-assignable (mutable references)
		var isRefType = globalDecl.Type is not null && globalDecl.Type.StartsWith("ref");
		var symbol = new VariableSymbol(globalDecl.Name, type, isMutable: globalDecl.IsMutable || isRefType)
		{
			IsInitialized = true,
			IsGlobal = true,
			Origin = OriginKind.Global,
			Visibility = globalDecl.Visibility,
			DeclaringUnit = context.CurrentUnit,
			DeclaringNamespace = context.CurrentNamespace
		};
		context.GlobalsByQualifiedName[qualifiedName] = symbol;
		if (!context.GlobalsByShortName.TryGetValue(globalDecl.Name, out var shortNameList))
		{
			shortNameList = [];
			context.GlobalsByShortName[globalDecl.Name] = shortNameList;
		}
		shortNameList.Add(symbol);
		context.GlobalVariables.Add((globalDecl, symbol));
	}

	// Multi-word containers are exactly those whose value spills past 8 bytes: slices/fat
	// pointers, interface references, and aggregates wider than a single machine word.
	private bool IsMultiWordContainer(TypeSymbol type)
	{
		switch (type)
		{
			case SliceTypeSymbol or InterfaceTypeSymbol:
				return true;
			case StructTypeSymbol or UnionTypeSymbol or EnumTypeSymbol:
				return ComputeByteSize(type, new HashSet<string>()) > 8;
			default:
				return false;
		}
	}

	// Best-effort recursive byte size for aggregates. Unknown/placeholder types are conservatively
	// treated as 8 bytes (single word) so the CVL1036 gate never fires spuriously.
	private int ComputeByteSize(TypeSymbol type, HashSet<string> seen)
	{
		if (type is PointerTypeSymbol or SliceTypeSymbol)
			return type is SliceTypeSymbol ? 16 : 8;

		if (type is ArrayTypeSymbol array)
			return array.Size == int.MaxValue ? 8 : array.Size * ComputeByteSize(array.ElementType, seen);

		if (type is EnumTypeSymbol enumType)
			return TypeSymbol.PrimitiveByteSize(enumType.StorageType);
		if (type is UnionTypeSymbol unionType)
		{
			if (!seen.Add(unionType.Name))
				return 8;
			var max = 0;
			foreach (var field in unionType.Fields)
				max = Math.Max(max, field.IsVoidVariant ? 0 : ComputeByteSize(field.Type, seen));
			return max;
		}
		if (type is StructTypeSymbol structType)
		{
			if (!seen.Add(structType.Name))
				return 8;
			var total = 0;
			foreach (var field in structType.Fields)
				total += ComputeByteSize(field.Type, seen);
			return total;
		}

		return TypeSymbol.PrimitiveByteSize(type);
	}

	private static bool IsCompileTimeConstant(ExpressionSyntax? expr)
	{
		if (expr is null)
			return true; // zero-initialized

		return expr switch
		{
			IntegerLiteralExpressionSyntax or DoubleLiteralExpressionSyntax or BooleanLiteralExpressionSyntax or CharacterLiteralExpressionSyntax or NullLiteralExpressionSyntax => true,
			UnaryExpressionSyntax { Operator: "-" } unary => IsCompileTimeConstant(unary.Operand),
			BinaryExpressionSyntax { Operator: "+" or "-" or "*" or "/" or "%" } bin
				=> IsCompileTimeConstant(bin.Left) && IsCompileTimeConstant(bin.Right),
			StructInitializationExpressionSyntax structInit => structInit.Initializers.All(static m => IsCompileTimeConstant(m.Expression)),
			_ => false
		};
	}

	/// <summary>
	/// True when the initializer divides or takes the remainder of a PURE-INTEGER constant by zero.
	/// Float literals switch the expression into IEEE context (1.0 / 0.0 yields +Inf and is legal),
	/// so the fold bails the moment it meets a non-integer node. Mirrors the wrapping arithmetic that
	/// CodeGenerator.BuildGlobalInitializer uses when it folds the same tree.
	/// </summary>
	private static bool ContainsConstantIntegerDivisionByZero(ExpressionSyntax? expr)
	{
		return expr switch
		{
			BinaryExpressionSyntax { Operator: "/" or "%" } bin
				=> (TryFoldIntegerConstant(bin.Right, out var divisor) && divisor == 0)
					|| ContainsConstantIntegerDivisionByZero(bin.Left)
					|| ContainsConstantIntegerDivisionByZero(bin.Right),
			BinaryExpressionSyntax { Operator: "+" or "-" or "*" } bin
				=> ContainsConstantIntegerDivisionByZero(bin.Left) || ContainsConstantIntegerDivisionByZero(bin.Right),
			UnaryExpressionSyntax { Operator: "-" } unary => ContainsConstantIntegerDivisionByZero(unary.Operand),
			StructInitializationExpressionSyntax structInit => structInit.Initializers.Any(static m => ContainsConstantIntegerDivisionByZero(m.Expression)),
			_ => false
		};
	}

	/// <summary>Folds a subtree of integer literals (wrapping) to a single value; false when the subtree
	/// contains any non-integer node (float literal, call, identifier, ...).</summary>
	private static bool TryFoldIntegerConstant(ExpressionSyntax expr, out long value)
	{
		switch (expr)
		{
			case IntegerLiteralExpressionSyntax lit:
				value = unchecked((long)lit.Value);
				return true;
			case UnaryExpressionSyntax { Operator: "-" } unary when TryFoldIntegerConstant(unary.Operand, out var inner):
				value = unchecked(-inner);
				return true;
			case BinaryExpressionSyntax bin when bin.Operator is "+" or "-" or "*" or "/" or "%"
				&& TryFoldIntegerConstant(bin.Left, out var l) && TryFoldIntegerConstant(bin.Right, out var r):
				value = bin.Operator switch
				{
					"+" => unchecked(l + r),
					"-" => unchecked(l - r),
					"*" => unchecked(l * r),
					"/" => unchecked(l / r),
					"%" => r == 0 ? 0 : unchecked(l % r),
					_ => 0
				};
				return true;
			default:
				value = 0;
				return false;
		}
	}


	/// <summary>
	/// Pass 0: Registers all declared namespaces, collects 'expose using' re-exports,
	/// and validates that:
	/// 1. 'expose using' is only declared inside a namespace (CVL1060).
	/// 2. Target namespaces of 'expose using' actually exist across the compilation units (CVL1061).
	/// </summary>
	private void ProcessExposeUsings(IEnumerable<CompilationUnitSyntax> units)
	{
		context.DeclaredNamespaces.Clear();
		context.NamespaceReExports.Clear();

		// 1. Gather all declared namespaces across all units
		foreach (var unit in units)
		{
			if (unit.NamespaceDeclaration is not null)
			{
				context.DeclaredNamespaces.Add(unit.NamespaceDeclaration.Name);
			}
		}

		// 2. Validate and register 'expose using' directives
		var exposeDirectives = new List<(CompilationContext FileCtx, UsingDirectiveSyntax Directive, string? EnclosingNs)>();

		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			var fileContext = context.FileContexts[unit];

			// File-level usings (outside any namespace)
			foreach (var u in unit.Usings)
			{
				if (u.IsExposed)
				{
					context.Diagnostics.Report(
						fileContext,
						u.Span,
						"'expose using' can only be used inside a namespace.",
						DiagnosticIds.ExposeUsingOutsideNamespace);
				}
			}

			// Namespace-level usings
			if (unit.NamespaceDeclaration is not null)
			{
				var currentNs = unit.NamespaceDeclaration.Name;
				foreach (var u in unit.NamespaceDeclaration.Usings)
				{
					if (u.IsExposed)
					{
						if (!context.NamespaceReExports.TryGetValue(currentNs, out var set))
						{
							set = new HashSet<string>(StringComparer.Ordinal);
							context.NamespaceReExports[currentNs] = set;
						}

						set.Add(u.NamespaceName);
						exposeDirectives.Add((fileContext, u, currentNs));
					}
				}
			}
		}

		// 3. Verify that re-export target namespaces exist (CVL1061)
		foreach (var (fileCtx, directive, _) in exposeDirectives)
		{
			if (!context.DeclaredNamespaces.Contains(directive.NamespaceName))
			{
				context.Diagnostics.Report(
					fileCtx,
					directive.Span,
					$"Target namespace '{directive.NamespaceName}' of 'expose using' does not exist.",
					DiagnosticIds.ExposeUsingNamespaceNotFound);
			}
		}
	}

}