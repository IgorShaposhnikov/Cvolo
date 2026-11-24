using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Declaration;

/// <summary>
/// Registers extension declarations and their declaration-time contract semantics during Pass 1.
/// </summary>
/// <remarks>
/// This service owns extension methods, constructors, destructors, protocol defaults, generic-extension
/// templates, and explicit nominal interface conformance. Raw type registration and free-function/FFI
/// registration remain separate declaration responsibilities.
/// </remarks>
internal sealed class ExtensionRegistrar(
	BindingContext context,
	FunctionDeclarationRegistrar functions,
	DestructorValidator destructors,
	GenericDefaultCopyValidator genericDefaultCopies)
{
	private readonly AttributeValidator _attributes = new(context);

	/// <summary>
	/// Maps declaration visibility to its ordering rank for extension-member narrowing checks.
	/// </summary>
	private static int VisibilityRank(Visibility visibility) => visibility switch
	{
		Visibility.Public => 2,
		Visibility.Internal => 1,
		_ => 0
	};

	/// <summary>
	/// Reports an extension-declaration diagnostic with a stable diagnostic identifier in the current unit.
	/// </summary>
	private void ReportDeclarationDiagnostic(SyntaxNode node, string message, string diagnosticId)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.Report(currentFileContext, node.Span, message, diagnosticId);
	}


	/// <summary>
	/// Registers one extension declaration, including methods, constructors, destructors,
	/// protocol defaults, generic-extension templates, and explicit nominal conformance.
	/// </summary>
	public void Declare(ExtensionDeclarationSyntax extDecl)
	{
		// Visibility: the extension block itself carries a modifier affecting all members
		// (default 'internal'). A member may only NARROW the block's visibility; any wider
		// member modifier is a CVL1031 error and the member takes the block level instead.
		var blockVisibility = extDecl.Visibility;
		var extendedType = context.ResolveType(extDecl.ExtendedTypeName);
		if (extendedType is null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, extDecl.NameSpan, $"Unknown type '{extDecl.ExtendedTypeName}' inside extension block.", DiagnosticIds.UnknownTypeInsideExtensionBlock);
			return;
		}

		// DEFER: Check if this is a generic struct OR generic union template
		if (context.GenericStructTemplates.ContainsKey(extendedType.Name) || context.GenericUnionTemplates.ContainsKey(extendedType.Name))
		{
			if (!context.GenericExtensionTemplates.TryGetValue(extendedType.Name, out var templates))
			{
				templates = [];
				context.GenericExtensionTemplates[extendedType.Name] = templates;
			}

			genericDefaultCopies.Validate(extDecl);

			// Operators are never generic, so a generic owner block cannot declare any of them
			// (spec §15/§16). Reject explicitly instead of dropping them silently with the block.
			RejectOperators(
				extDecl,
				op => $"Operator '{op.Operator}' cannot be declared in generic extension '{extDecl.ExtendedTypeName}': operator overloads do not support generic owners.",
				DiagnosticIds.OperatorGenericNotSupported);

			templates.Add(extDecl);
			context.GenericExtensionTemplateUnits[extDecl] = context.CurrentUnit!;
			return;
		}

		// PROTOCOL DEFAULTS: an extension block written directly ON a protocol
		// definition provides default implementations. Conforming concrete types
		// inherit them unless they define a matching method of their own. The
		// naked "{Protocol}.{Method}" symbols registered below are never emitted
		// (CodeGenerator skips protocol-typed extension blocks); ValidationPass
		// materializes a substituted copy onto each conforming concrete type.
		if (extendedType is ProtocolTypeSymbol protoExtType)
		{
			if (!context.ProtocolDefaults.TryGetValue(protoExtType.Name, out var defaults))
			{
				defaults = [];
				context.ProtocolDefaults[protoExtType.Name] = defaults;
			}

			// Associated functions are receiverless, so they cannot be protocol defaults; the
			// conformance surface of a protocol is entirely receiver-backed.
			foreach (var method in extDecl.Methods)
			{
				if (method.IsAssociated)
				{
					ReportDeclarationDiagnostic(
						method,
						$"Associated function '{extDecl.ExtendedTypeName}.{method.Name}' cannot be declared in an extension block on a protocol.",
						DiagnosticIds.AssociatedFunctionInProtocolExtension);
					continue;
				}

				defaults.Add((method.Name, method));
			}
		}

		// RETROACTIVE CONFORMANCE: "extension T : IName" records that the
		// extended concrete type conforms to the named interface and validates
		// that this extension provides every required method with a matching
		// signature. (Generic conformance `extension Pair<T> : IFoo` is deferred.)
		if (extDecl.ConformsTo is not null)
			RegisterConformance(extDecl, extendedType);

		// OPERATORS: an operator overload is a receiverless associated callable, so it validates
		// once up front and then enters the registration loop as an ordinary associated function
		// carrying its stable operator token as the source name.
		var operatorFunctions = PrepareOperatorFunctions(extDecl, extendedType);

		// Destructors register as ordinary extension methods named "~T" (void, this-only)
		foreach (var method in extDecl.Methods
			.Concat(extDecl.Destructors.Select(static d => d.ToFunctionDeclaration()))
			.Concat(operatorFunctions))
		{
			var isDestructor = method.Name.StartsWith('~');
			if (isDestructor && !destructors.ValidateDeclaration(extDecl.ExtendedTypeName, method))
				continue;

			// An associated function has no receiver at all: no synthetic 'this', no receiver
			// contract, and a separate overload table so it never merges with receiver-backed
			// candidates that share the same source name.
			var isAssociated = method.IsAssociated;
			if (isAssociated && method.Receiver != ReceiverContract.None)
			{
				ReportDeclarationDiagnostic(
					method,
					$"Associated function '{extDecl.ExtendedTypeName}.{method.Name}' cannot declare an instance receiver.",
					DiagnosticIds.AssociatedFunctionWithReceiver);
				continue;
			}

			// RESERVED: `Invoke` is the compiler-owned synthesized invocation member of every
			// delegate type (including native delegates). A user extension of that name would
			// shadow it, so it is rejected rather than registered.
			if (method.Name == "Invoke" && extendedType is DelegateTypeSymbol)
			{
				ReportDeclarationDiagnostic(
					method,
					$"Extension method 'Invoke' cannot be declared on delegate type '{extDecl.ExtendedTypeName}': 'Invoke' is the compiler-owned delegate invocation member.",
					DiagnosticIds.ExtensionInvokeOnDelegateTypeIsReserved);
				continue;
			}

			// Mangled name represents the scoped path, e.g., "MyNamespace.Point.Move"
			var baseMangledName = context.GetMangledName($"{extDecl.ExtendedTypeName}.{method.Name}", context.CurrentNamespace);

			// A member that declares its own type parameters cannot be registered yet: its parameter and
			// return types may name those parameters, and no concrete mangled signature exists until a call
			// site supplies type arguments. Park it as a template on the owner; the call site instantiates it
			// into the correct overload table (associated or instance) with the arguments already substituted.
			if (method.GenericParameters.Count > 0 && method.GenericParameters.Any(p => context.ResolveType(p) is null))
			{
				if (!context.GenericExtensionMethodTemplates.TryGetValue(baseMangledName, out var methodTemplates))
				{
					methodTemplates = [];
					context.GenericExtensionMethodTemplates[baseMangledName] = methodTemplates;
				}

				methodTemplates.Add(new GenericExtensionMethodTemplate(method, extendedType, baseMangledName));
				continue;
			}

			// 1. Inject the implicit first parameter: "this"
			// It starts as a read-only pointer. The ValidationPass will upgrade it to mutable if needed!
			// Associated functions skip this entirely: they are receiverless by construction.
			var methodTier = method.Modifier ?? SafetyTier.Safe;
			if (method.Attributes.Any(a => _attributes.NormalizeName(a.Name) == "UnsafeBody"))
				methodTier = SafetyTier.Unsafe;
			var parameters = new List<ParameterSymbol>();
			if (!isAssociated)
				parameters.Add(new ParameterSymbol("this", new PointerTypeSymbol(extendedType, isMutable: false)));

			foreach (var param in method.Parameters)
			{
				var paramSymbol = functions.CreateParameter(param, methodTier);
				if (paramSymbol is null)
				{
					ReportDeclarationDiagnostic(param, $"Unknown parameter type '{param.Type}'", DiagnosticIds.UnknownParameterType);
					continue;
				}

				parameters.Add(paramSymbol);
			}

			var returnType = context.ResolveType(method.ReturnType);
			if (returnType is null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, method.ReturnTypeSpan, $"Unknown return type '{method.ReturnType}'", DiagnosticIds.UnknownReturnType);
				return;
			}

			// 2. Register the overloaded, parameter-mangled global signature
			var overloadedName = context.GetOverloadedMangledName(baseMangledName, parameters.Select(p => p.Type).ToList());

			var memberVisibility = method.SyntacticVisibility ?? blockVisibility;
			if (!context.LegacyVisibility && method.SyntacticVisibility is { } memberVis && VisibilityRank(memberVis) > VisibilityRank(blockVisibility))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, method.NameSpan,
					$"Element '{method.Name}' cannot declare a wider visibility modifier than its enclosing extension block visibility level ({blockVisibility}).",
					DiagnosticIds.VisibilityExpansionInExtension);
			}

			var newSymbol = new FunctionSymbol(overloadedName, returnType, parameters)
			{
				Visibility = memberVisibility,
				DeclaringUnit = context.CurrentUnit,
				CallableKind = isAssociated ? CallableKind.Associated : CallableKind.InstanceExtension
			};
			var methodSuppressedWarnings = new List<string>();
			_attributes.ApplyFunctionAttributes(
				_attributes.Verify(method.Attributes, isDestructor ? "Destructor" : "Method", methodSuppressedWarnings, methodTier),
				newSymbol,
				methodSuppressedWarnings,
				method.Attributes);
			// CVL1001 is emitted by the validation pass after the body's unsafe usages are known.

			// COLLISION RULE: an extension may not re-declare a method the type already
			// has with a matching signature (another extension block, the proto-default
			// registry's naked symbols, or a conformer-declared override). First wins.
			// Instance and associated overload sets are disjoint by construction, so the
			// same source name may legitimately exist once in each form.
			if (context.Globals.Lookup(overloadedName) is not null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, method.NameSpan,
					$"Duplicate symbol '{method.Name}' on type '{extDecl.ExtendedTypeName}' in extension blocks.", DiagnosticIds.DuplicateSymbolOnTypeInExtensionBlocks);
				continue;
			}

			context.Globals.Declare(newSymbol);

			var overloadTable = isAssociated ? context.AssociatedFunctions : context.OverloadedFunctions;
			if (!overloadTable.TryGetValue(baseMangledName, out var candidates))
			{
				candidates = [];
				overloadTable[baseMangledName] = candidates;
			}

			candidates.Add(newSymbol);

			context.SymbolUnits[overloadedName] = context.CurrentUnit!;

			if (isDestructor)
			{
				destructors.Register(extDecl.ExtendedTypeName, newSymbol, extendedType);
			}
		}

		foreach (var ctorDecl in extDecl.Constructors)
		{
			if (ctorDecl.StructName != extDecl.ExtendedTypeName)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, ctorDecl.NameSpan, $"Constructor name '{ctorDecl.StructName}' must match the extended type '{extDecl.ExtendedTypeName}'.", DiagnosticIds.ConstructorNameMustMatchTheExtendedType);
				continue;
			}

			if (extendedType is not StructTypeSymbol ctorStructType)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, ctorDecl.NameSpan, $"Cannot define a constructor for non-struct type '{extDecl.ExtendedTypeName}'.", DiagnosticIds.CannotDefineAConstructorForNonStructType);
				continue;
			}

			// Mangled base name is just the struct name: bare calls 'T(args)' resolve like free functions
			var ctorBaseMangledName = context.GetMangledName(extDecl.ExtendedTypeName, context.CurrentNamespace);

			// 1. Inject the implicit first parameter: "this" (the destination storage)
			var ctorThisParamType = new PointerTypeSymbol(ctorStructType, isMutable: true);
			var ctorParameters = new List<ParameterSymbol> { new ParameterSymbol("this", ctorThisParamType) };

			var hasBadParam = false;
			foreach (var param in ctorDecl.Parameters)
			{
				var paramSymbol = functions.CreateParameter(param);
				if (paramSymbol is null)
				{
					ReportDeclarationDiagnostic(param, $"Unknown parameter type '{param.Type}'", DiagnosticIds.UnknownParameterType);
					hasBadParam = true;
					continue;
				}

				ctorParameters.Add(paramSymbol);
			}

			if (hasBadParam)
				continue;

			// 2. Register under the struct's name so 'T(...)' call sites resolve via existing overload machinery
			var ctorOverloadedName = context.GetOverloadedMangledName(ctorBaseMangledName, ctorParameters.Select(p => p.Type).ToList());
			var ctorVisibility = ctorDecl.SyntacticVisibility ?? blockVisibility;
			if (!context.LegacyVisibility && ctorDecl.SyntacticVisibility is { } ctorVis && VisibilityRank(ctorVis) > VisibilityRank(blockVisibility))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, ctorDecl.NameSpan,
					$"Element '{ctorDecl.StructName}' cannot declare a wider visibility modifier than its enclosing extension block visibility level ({blockVisibility}).",
					DiagnosticIds.VisibilityExpansionInExtension);
			}

			var ctorSymbol = new FunctionSymbol(ctorOverloadedName, ctorStructType, ctorParameters)
			{
				Visibility = ctorVisibility,
				DeclaringUnit = context.CurrentUnit
			};
			var ctorSuppressedWarnings = new List<string>();
			_attributes.ApplyFunctionAttributes(_attributes.Verify(ctorDecl.Attributes, "Constructor", ctorSuppressedWarnings), ctorSymbol, ctorSuppressedWarnings);
			// CVL1001 is emitted by the validation pass after the body's unsafe usages are known.

			// COLLISION RULE: duplicate constructor signatures on the same type.
			if (context.Globals.Lookup(ctorOverloadedName) is not null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, ctorDecl.NameSpan,
					$"Duplicate constructor signature for type '{extDecl.ExtendedTypeName}'.", DiagnosticIds.DuplicateConstructorSignatureForType);
				continue;
			}

			context.Globals.Declare(ctorSymbol);

			if (!context.OverloadedFunctions.TryGetValue(ctorBaseMangledName, out var ctorCandidates))
			{
				ctorCandidates = [];
				context.OverloadedFunctions[ctorBaseMangledName] = ctorCandidates;
			}

			ctorCandidates.Add(ctorSymbol);

			if (context.CurrentUnit is not null)
			{
				context.SymbolUnits[ctorOverloadedName] = context.CurrentUnit;
			}

			if (!context.Constructors.TryGetValue(extDecl.ExtendedTypeName, out var registeredCtors))
			{
				registeredCtors = [];
				context.Constructors[extDecl.ExtendedTypeName] = registeredCtors;
			}

			registeredCtors.Add(ctorSymbol);
		}
	}

	/// <summary>
	/// Validates the operator overloads declared by an extension block and converts the accepted
	/// ones into the associated function declarations consumed by the registration loop.
	/// Rejected operators are reported here and dropped, so the loop itself stays unaware of the
	/// operator rule set.
	/// </summary>
	private List<FunctionDeclarationSyntax> PrepareOperatorFunctions(ExtensionDeclarationSyntax extDecl, TypeSymbol extendedType)
	{
		var prepared = new List<FunctionDeclarationSyntax>(extDecl.Operators.Count);
		if (extDecl.Operators.Count == 0)
			return prepared;

		// A protocol member describes receiver-backed behavior, and an operator is receiverless.
		if (extendedType is ProtocolTypeSymbol)
		{
			RejectOperators(
				extDecl,
				op => $"Operator '{op.Operator}' cannot be declared in an extension block on protocol '{extDecl.ExtendedTypeName}'.",
				DiagnosticIds.AssociatedFunctionInProtocolExtension);
			return prepared;
		}

		// OWNERSHIP (spec §16): operators are not a globally open extension point, so only the
		// package or project that declares the owner type may define its operators.
		if (IsForeignOperatorOwner(extendedType.Name, context.CurrentUnit))
		{
			RejectOperators(
				extDecl,
				op => $"Operator '{op.Operator}' for '{extDecl.ExtendedTypeName}' must be declared in the same package or project that declares the type.",
				DiagnosticIds.OperatorForeignOwner);
			return prepared;
		}

		foreach (var op in extDecl.Operators)
		{
			// Shapes the parser cannot represent (e.g. a receiver parameter) are already reported.
			if (op.HasSyntaxError)
				continue;

			// 1. ARITY: the parameter count decides the operator's position, so a wrong count is
			//    the more fundamental error and is reported before the position-specific lookup.
			if (op.Parameters.Count is 0 or > 2)
			{
				ReportDeclarationDiagnostic(
					op,
					$"Operator '{op.Operator}' must declare one operand (unary) or two operands (binary), but declares {op.Parameters.Count}.",
					DiagnosticIds.OperatorArityMismatch);
				continue;
			}

			// 2. POSITION: the spelling must be overloadable in the position its arity implies.
			//    Comparison and shift operators are binary-only, '!' and '~' are unary-only, and
			//    assignment is never overloadable. The parser derived its token with the same
			//    (spelling, position) pair, so an accepted operator already carries this token.
			if (OperatorTokens.TryGetToken(op.Operator, op.IsUnary) is null)
			{
				var isAssignment = op.Operator == "=";
				ReportDeclarationDiagnostic(
					op,
					isAssignment
						? $"Assignment operator '{op.Operator}' cannot be overloaded: assignment is compiler-owned and participates in initialization, copy/move semantics, lifetime, destruction, mutability, references and borrow analysis."
						: $"Operator '{op.Operator}' cannot be declared with {(op.IsUnary ? "one operand" : "two operands")}.",
					isAssignment ? DiagnosticIds.OperatorAssignmentNotOverloadable : DiagnosticIds.OperatorArityMismatch);
				continue;
			}

			// 3. COHERENCE (spec §16): the owner must be one of the operand types, otherwise the
			//    overload would answer for operands that have nothing to do with it. Operands that
			//    do not resolve are ignored here; the registration loop reports them as unknown.
			var operandTypes = op.Parameters.Select(p => context.ResolveType(p.Type)).ToList();
			if (operandTypes.Any(static t => t is not null) && !operandTypes.Any(t => t?.Name == extendedType.Name))
			{
				ReportDeclarationDiagnostic(
					op,
					$"Operator '{op.Operator}' may only be declared in an extension whose owner type is one of its operand types: '{extDecl.ExtendedTypeName}' is not an operand of operator '{op.Operator}'.",
					DiagnosticIds.OperatorOwnerNotOperand);
				continue;
			}

			prepared.Add(op.ToFunctionDeclaration());
		}

		return prepared;
	}

	/// <summary>
	/// Decides whether an operator's owner type belongs to a different ownership scope than the
	/// extension that declares it. Ownership is per package or project, not per file: the source
	/// files of one project share a scope, and a package owns both its API stub and its template
	/// source, so an operator declared in either one still belongs to the package that owns the
	/// type. A type with no recorded declaring unit (a builtin, a protocol) has no foreign owner.
	/// </summary>
	private bool IsForeignOperatorOwner(string ownerName, CompilationUnitSyntax? declaringUnit)
	{
		if (!context.SymbolUnits.TryGetValue(ownerName, out var ownerUnit) || ownerUnit == declaringUnit)
			return false;

		var declaringIsPackaged = context.TryGetUnitPackageId(declaringUnit, out var declaringPackage);
		var ownerIsPackaged = context.TryGetUnitPackageId(ownerUnit, out var ownerPackage);

		// Both units are plain project source: one project owns both, however many files they span.
		if (!declaringIsPackaged && !ownerIsPackaged)
			return false;

		// One unit is packaged and the other is not, or the two packages differ.
		return !declaringIsPackaged
			|| !ownerIsPackaged
			|| !string.Equals(declaringPackage, ownerPackage, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Reports one diagnostic per operator of an extension block and discards all of them.
	/// </summary>
	private void RejectOperators(ExtensionDeclarationSyntax extDecl, Func<OperatorDeclarationSyntax, string> message, string diagnosticId)
	{
		if (extDecl.Operators.Count == 0)
			return;

		foreach (var op in extDecl.Operators)
			ReportDeclarationDiagnostic(op, message(op), diagnosticId);
	}

	/// <summary>
	/// Records and validates nominal interface conformance declared by an extension block.
	/// </summary>
	private void RegisterConformance(ExtensionDeclarationSyntax extDecl, TypeSymbol extendedType)
	{
		// Resolve the interface within the extension's declaration context.
		var prevUnit = context.CurrentUnit;
		var prevNs = context.CurrentNamespace;
		context.CurrentUnit = context.SymbolUnits.TryGetValue(extendedType.Name, out var extUnit) ? extUnit : context.CurrentUnit;
		context.CurrentNamespace = context.CurrentUnit?.NamespaceDeclaration?.Name;
		var extensionUnit = context.CurrentUnit;

		var interfaceType = context.ResolveType(extDecl.ConformsTo!);
		context.CurrentUnit = prevUnit;
		context.CurrentNamespace = prevNs;

		if (interfaceType is not InterfaceTypeSymbol interfaceSymbol)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];

			// The conformance clause names the capability, not the whole block, so the diagnostic
			// points at ConformsToSpan. A name that is not an interface gets an explanation of what
			// the clause actually accepts instead of a bare "unknown".
			switch (interfaceType)
			{
				case ProtocolTypeSymbol:
					context.Diagnostics.Report(currentFileContext, extDecl.ConformsToSpan,
						$"Extension block cannot implement protocol '{extDecl.ConformsTo}': protocols are satisfied structurally, so only an interface can be named in a conformance declaration.",
						DiagnosticIds.ConformanceTargetNotInterface);
					break;
				case null:
					context.Diagnostics.Report(currentFileContext, extDecl.ConformsToSpan,
						$"Unknown interface '{extDecl.ConformsTo}' in conformance declaration. Only an interface can be named here.",
						DiagnosticIds.ConformanceUnknownInterface);
					break;
				default:
					context.Diagnostics.Report(currentFileContext, extDecl.ConformsToSpan,
						$"'{extDecl.ConformsTo}' is not an interface; only an interface can be named in a conformance declaration.",
						DiagnosticIds.ConformanceTargetNotInterface);
					break;
			}

			return;
		}

		// An open specialization belongs to a generic owner; it is materialized for each
		// concrete owner by BindingContext.MonomorphizeExtensionsForType instead.
		if (interfaceSymbol.Declaration?.GenericParameters.Count > 0 && !interfaceSymbol.IsClosed)
			return;

		// Members are validated before any evidence is published; the requires-clause is
		// resolved later so forward-declared conformances are order-independent.
		context.QueueInterfaceConformance(extendedType, interfaceSymbol, extDecl, extensionUnit, new Dictionary<string, TypeSymbol>());
	}
}
