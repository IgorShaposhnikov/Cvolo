using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes;

public sealed class DeclarationPass(BindingContext context)
{
	public void Process(IEnumerable<CompilationUnitSyntax> units)
	{
		// Pass 0: Register all Structs across all files
		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			context.CurrentNamespace = unit.NamespaceDeclaration?.Name;

			var members = context.CurrentNamespace != null ? unit.NamespaceDeclaration!.Members : unit.Members;
			foreach (var member in members)
			{
				if (member is StructDeclarationSyntax structDecl)
					DeclareStruct(structDecl);
				else if (member is UnionDeclarationSyntax unionDecl)
					DeclareUnion(unionDecl);
				else if (member is InterfaceDeclarationSyntax interfaceDecl)
					DeclareInterface(interfaceDecl);
				else if (member is ProtocolDeclarationSyntax protocolDecl)
					DeclareProtocol(protocolDecl);
			}
		}

		// Pass 1: Register all Function/Extern signatures across all files
		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			context.CurrentNamespace = unit.NamespaceDeclaration?.Name;

			var members = unit.NamespaceDeclaration != null ? unit.NamespaceDeclaration.Members : unit.Members;
			foreach (var member in members)
			{
				if (member is FunctionDeclarationSyntax func)
					DeclareFunction(func);
				else if (member is ExternDeclarationSyntax ext)
					DeclareExternFunction(ext);
				else if (member is ExtensionDeclarationSyntax extDecl)
					DeclareExtension(extDecl);
				else if (member is GlobalVariableDeclarationSyntax globalDecl)
					DeclareGlobalVariable(globalDecl);
			}
		}
	}

	private void DeclareStruct(StructDeclarationSyntax structDecl)
	{
		var mangledName = context.GetMangledName(structDecl.Name, context.CurrentNamespace);

		if (context.StructTypes.ContainsKey(mangledName) || TypeSymbol.FromName(structDecl.Name) is not null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, structDecl.Span, $"Duplicate type definition '{structDecl.Name}'");
			return;
		}

		// No intrinsic targets structs; verify so unknown/misapplied attributes still surface.
		_ = VerifyAttributes(structDecl.Attributes, "Struct", new List<string>());

		// If this is a generic struct template (e.g. struct Point<T>)
		if (structDecl.GenericParameters.Count > 0)
		{
			// Record the original template file unit
			context.SymbolUnits[mangledName] = context.CurrentUnit!;

			context.GenericStructTemplates[mangledName] = structDecl;

			var placeholderFields = new List<StructFieldSymbol>();
			var templateSymbol = new StructTypeSymbol(mangledName, placeholderFields);
			context.StructTypes[mangledName] = templateSymbol;
			return;
		}

		context.SymbolUnits[mangledName] = context.CurrentUnit!;

		// Register a placeholder symbol BEFORE resolving fields so that
		// self-referential field types (e.g. `Option<ref Node>`) can resolve
		// the enclosing struct's own name during generic instantiation.
		// The fully-built symbol replaces it below; equality/codegen are
		// name-driven, so the placeholder identity is invisible downstream.
		context.StructTypes[mangledName] = new StructTypeSymbol(mangledName, []);

		var fields = new List<StructFieldSymbol>();
		var fieldNames = new HashSet<string>();

		foreach (var field in structDecl.Fields)
		{
			if (!fieldNames.Add(field.Name))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, field.Span, $"Duplicate field '{field.Name}' in struct '{structDecl.Name}'");
				continue;
			}

			var fieldType = context.ResolveType(field.Type);
			if (fieldType is null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, field.Span, $"Unknown type '{field.Type}' of field '{field.Name}'");
				continue;
			}

			fields.Add(new StructFieldSymbol(field.Name, fieldType));
		}

		var structSymbol = new StructTypeSymbol(mangledName, fields);
		context.StructTypes[mangledName] = structSymbol;
		// Ensure the canonical cache no longer serves the (empty) placeholder
		// registered above, or later ResolveType("Node") would return a
		// field-less Node even after this replacement.
		context.ReplaceTypeInCache(mangledName, structSymbol);
	}

	private void DeclareInterface(InterfaceDeclarationSyntax interfaceDecl)
	{
		var mangledName = context.GetMangledName(interfaceDecl.Name, context.CurrentNamespace);

		if (context.InterfaceTypes.ContainsKey(mangledName))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, interfaceDecl.Span, $"Duplicate interface definition '{interfaceDecl.Name}'");
			return;
		}

		context.SymbolUnits[mangledName] = context.CurrentUnit!;
		context.InterfaceTemplates[mangledName] = interfaceDecl;
		context.InterfaceTypes[mangledName] = new InterfaceTypeSymbol(mangledName);
	}

	private void DeclareProtocol(ProtocolDeclarationSyntax protocolDecl)
	{
		var mangledName = context.GetMangledName(protocolDecl.Name, context.CurrentNamespace);

		if (context.ProtocolTypes.ContainsKey(mangledName))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, protocolDecl.Span, $"Duplicate protocol definition '{protocolDecl.Name}'");
			return;
		}

		context.SymbolUnits[mangledName] = context.CurrentUnit!;
		context.ProtocolTemplates[mangledName] = protocolDecl;

		// Phase-1 structural pre-match: build the canonical member tokens once,
		// resolved in this protocol's namespace, so conformance checks are O(1)
		// set membership (topological, naming-independent) rather than symbolic.
		var canonicalMembers = ProtocolCanonicalizer.BuildCanonicalMembers(protocolDecl, context);
		context.ProtocolTypes[mangledName] = new ProtocolTypeSymbol(mangledName, protocolDecl.Members, protocolDecl.GenericParameters, protocolDecl.Constraint, canonicalMembers);
	}

	private void DeclareFunction(FunctionDeclarationSyntax func)
	{
		// Entry point (main / Main) is always global, lowercase, and unmangled
		var mangledName = func.Name == "main" || func.Name == "Main"
			? "main"
			: context.GetMangledName(func.Name, context.CurrentNamespace);

		// If this is a generic function template, register it as a template
		if (func.GenericParameters.Count > 0)
		{
			// Check if all generic parameters are concrete types (explicit specialization)
			var isSpecialization = func.GenericParameters.All(p => context.ResolveType(p) != null);

			if (isSpecialization)
			{
				var rawName = $"{mangledName}<{string.Join(",", func.GenericParameters)}>";
				// Canonical Name
				var instName = context.NormalizeGenericName(rawName);

				var returnType = context.ResolveType(func.ReturnType);
				context.SymbolUnits[mangledName] = context.CurrentUnit!;
				if (returnType is null)
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, func.Span, $"Unknown return type '{func.ReturnType}'");
					return;
				}

				var specParameters = new List<ParameterSymbol>();
				foreach (var param in func.Parameters)
				{
					var paramType = context.ResolveType(param.Type);
					if (paramType is null)
					{
						var currentFileContext = context.FileContexts[context.CurrentUnit!];
						context.Diagnostics.Report(currentFileContext, param.Span, $"Unknown parameter type '{param.Type}'");
						continue;
					}

					specParameters.Add(new ParameterSymbol(param.Name, paramType));
				}

				var instSymbol = new FunctionSymbol(instName, returnType!, specParameters);
				context.MonomorphizedFunctions[instName] = instSymbol;

				var instDecl = new FunctionDeclarationSyntax(func.Span, func.ReturnType, instName, [], func.Parameters, func.Body, modifier: func.Modifier);
				context.MonomorphizedFunctionDecls.Add(instDecl);
				return;
			}

			// Record the original template file unit
			context.SymbolUnits[mangledName] = context.CurrentUnit!;

			context.GenericFunctionTemplates[mangledName] = func;
			return;
		}

		// A function with any nominal-interface-typed parameter is an implicit generic template:
		// the interface name has no value representation, so it is monomorphized at each call site
		// with the concrete conforming argument type (static-only dispatch, no vtable).
		if (func.Parameters.Any(p => IsInterfaceTypedParameter(p)))
		{
			context.SymbolUnits[mangledName] = context.CurrentUnit!;
			context.InterfaceFunctionTemplates[mangledName] = func;
			return;
		}

		// A function with any protocol-typed parameter is likewise an implicit
		// generic template: a protocol name has no value representation, so it is
		// monomorphized at each call site with the structurally conforming
		// concrete argument type (static-only dispatch, no vtable).
		if (func.Parameters.Any(p => IsProtocolTypedParameter(p)))
		{
			context.SymbolUnits[mangledName] = context.CurrentUnit!;
			context.ProtocolFunctionTemplates[mangledName] = func;
			return;
		}

		context.SymbolUnits[mangledName] = context.CurrentUnit!;
		var type = context.ResolveType(func.ReturnType);
		if (type is null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, func.Span, $"Unknown return type '{func.ReturnType}'");
			return;
		}

		var parameters = new List<ParameterSymbol>();
		foreach (var param in func.Parameters)
		{
			var paramSymbol = CreateParameter(param);
			if (paramSymbol is null)
			{
				ReportDeclarationDiagnostic(param, $"Unknown parameter type '{param.Type}'");
				continue;
			}

			parameters.Add(paramSymbol);
		}

		var overloadedMangledName = context.GetOverloadedMangledName(mangledName, parameters.Select(p => p.Type).ToList());

		var existing = context.Globals.Lookup(overloadedMangledName);
		if (existing is not null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, func.Span, $"Duplicate definition of function '{func.Name}' with a matching parameter signature.");
			return;
		}

		// Determine safety tier from function modifier
		var safetyTier = func.Modifier ?? SafetyTier.Safe;

		var newSymbol = new FunctionSymbol(overloadedMangledName, type, parameters) { SafetyTier = safetyTier };
		var suppressedWarnings = new List<string>();
		ApplyFunctionAttributes(VerifyAttributes(func.Attributes, "Function", suppressedWarnings, safetyTier), newSymbol, suppressedWarnings);

		// [UnsafeBody] promotes to Unsafe tier even without the unsafe modifier
		if (newSymbol.IsUnsafeBody)
			newSymbol.SafetyTier = SafetyTier.Unsafe;

		WarnIfUnsafeBodyUnused(func.Span, func.Body, newSymbol, suppressedWarnings);

		// Warn if 'unbound' is used but no ref/refvar parameters exist
		if (safetyTier == SafetyTier.Unbound && !suppressedWarnings.Contains(DiagnosticIds.UnboundNoRefParams))
		{
			var hasRefParams = parameters.Any(p => p.Type is PointerTypeSymbol { IsMutable: true } or PointerTypeSymbol { IsMutable: false });
			if (!hasRefParams)
			{
				ReportDeclarationWarning(func, "'unbound' modifier has no effect because function has no ref/refvar parameters.", DiagnosticIds.UnboundNoRefParams);
			}
		}

		context.Globals.Declare(newSymbol);

		if (!context.OverloadedFunctions.TryGetValue(mangledName, out var candidates))
		{
			candidates = [];
			context.OverloadedFunctions[mangledName] = candidates;
		}

		candidates.Add(newSymbol);
	}

	// A parameter is interface-typed if its (possibly ref/refvar-wrapped) type resolves to an
	// InterfaceTypeSymbol. Functions carrying such a parameter become implicit generic templates.
	private bool IsInterfaceTypedParameter(ParameterSyntax p)
	{
		var type = p.Type;
		if (type.StartsWith("refvar ", StringComparison.Ordinal)) type = type[7..];
		else if (type.StartsWith("ref ", StringComparison.Ordinal)) type = type[4..];
		return context.ResolveType(type) is InterfaceTypeSymbol;
	}

	// A parameter is protocol-typed if its (possibly ref/refvar-wrapped) type resolves to a
	// ProtocolTypeSymbol. Functions carrying such a parameter become implicit generic templates,
	// monomorphized per call site against the structurally conforming concrete type.
	private bool IsProtocolTypedParameter(ParameterSyntax p)
	{
		var type = p.Type;
		if (type.StartsWith("refvar ", StringComparison.Ordinal)) type = type[7..];
		else if (type.StartsWith("ref ", StringComparison.Ordinal)) type = type[4..];
		return context.ResolveType(type) is ProtocolTypeSymbol;
	}

	// M1 attribute model: only System.* intrinsics exist. Their [AttributeUsage]-style rules
	// (syntactic target x safety context, per spec section 4) are modeled compiler-side until
	// the language has enums/inheritance to declare them in source. Attributes are erased
	// before emission - they never reach LLVM IR.
	private static readonly Dictionary<string, (string[] Targets, SafetyTier[] Contexts)> IntrinsicAttributes = new()
	{
		["UnsafeBody"] = (["Function", "Method", "Constructor", "Destructor"], [SafetyTier.Safe, SafetyTier.Unbound, SafetyTier.Unsafe]),
		["NoAlias"] = (["Function", "Method", "Parameter"], [SafetyTier.Unbound, SafetyTier.Unsafe]),
		["SuppressWarning"] = (["Struct", "Function", "Method", "Constructor", "Destructor", "Parameter"], [SafetyTier.Safe, SafetyTier.Unbound, SafetyTier.Unsafe])
	};

	private static readonly HashSet<string> KnownWarningIds = [DiagnosticIds.UnsafeBodyNoEffect, DiagnosticIds.UnknownAttribute, DiagnosticIds.UnboundNoRefParams];

	private static string? NormalizeAttributeName(string attributeName)
	{
		var simple = attributeName.Contains('.') ? attributeName[(attributeName.LastIndexOf('.') + 1)..] : attributeName;
		if (simple.EndsWith("Attribute", StringComparison.Ordinal))
			simple = simple[..^"Attribute".Length];

		return IntrinsicAttributes.ContainsKey(simple) ? simple : null;
	}

	/// <summary>Verifies each attribute against its syntactic attach point and returns the canonical keys that passed.</summary>
	private List<string> VerifyAttributes(IReadOnlyList<AttributeSyntax> attributes, string syntacticTarget, List<string>? suppressedWarnings = null, SafetyTier safetyTier = SafetyTier.Safe)
	{
		var applied = new List<string>();
		var seen = new HashSet<string>();
		var unknownAttributes = new List<AttributeSyntax>();
		foreach (var attr in attributes)
		{
			var key = NormalizeAttributeName(attr.Name);
			if (key is null)
			{
				// Hybrid stance: unknown names are accepted and erased at codegen, but
				// flagged so typos stay visible. Reported after the loop so a
				// [SuppressWarning] anywhere in the same list silences it regardless of order.
				unknownAttributes.Add(attr);
				continue;
			}

			if (!seen.Add(key))
			{
				ReportDeclarationDiagnostic(attr, $"Duplicate attribute '[{key}]'.");
				continue;
			}

			var (targets, contexts) = IntrinsicAttributes[key];
			if (!targets.Contains(syntacticTarget))
			{
				ReportDeclarationDiagnostic(attr, $"Attribute '[{key}]' cannot be applied to {syntacticTarget.ToLowerInvariant()} declarations.");
				continue;
			}

			if (!contexts.Contains(safetyTier))
			{
				ReportDeclarationDiagnostic(attr, $"Attribute '[{key}]' cannot be applied in {safetyTier} context.");
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

			ReportDeclarationWarning(unknown, $"Unknown attribute '{unknown.Name}'; it will be ignored.", DiagnosticIds.UnknownAttribute);
		}

		return applied;
	}

	private void ApplySuppressWarning(AttributeSyntax attr, List<string>? suppressedWarnings)
	{
		if (attr.Arguments.Count != 1 || attr.Arguments[0] is not StringLiteralExpressionSyntax literal)
		{
			ReportDeclarationDiagnostic(attr, "Attribute '[SuppressWarning]' requires exactly one string literal argument.");
			return;
		}

		var warningId = literal.Value;
		if (!KnownWarningIds.Contains(warningId))
		{
			ReportDeclarationDiagnostic(attr, $"Unknown warning id '{warningId}'.");
			return;
		}

		suppressedWarnings?.Add(warningId);
	}

	private static void ApplyFunctionAttributes(List<string> appliedKeys, FunctionSymbol symbol, List<string> suppressedWarnings)
	{
		if (appliedKeys.Contains("UnsafeBody"))
			symbol.IsUnsafeBody = true;

		if (appliedKeys.Contains("NoAlias"))
			symbol.IsNoAlias = true;

		foreach (var warningId in suppressedWarnings)
			symbol.SuppressedWarnings.Add(warningId);
	}

	/// <summary>Flags [UnsafeBody] declarations whose bodies contain nothing unsafe; suppressible via [SuppressWarning].</summary>
	private void WarnIfUnsafeBodyUnused(Core.Diagnostics.TextSpan declarationSpan, SyntaxNode body, FunctionSymbol symbol, List<string> suppressedWarnings)
	{
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

	/// <summary>Resolves a parameter's type, verifies its attributes, and returns null when the type is unknown.</summary>
	private ParameterSymbol? CreateParameter(ParameterSyntax param)
	{
		var paramType = context.ResolveType(param.Type);
		if (paramType is null)
			return null;

		var symbol = new ParameterSymbol(param.Name, paramType);
		var paramSuppressedWarnings = new List<string>();
		if (VerifyAttributes(param.Attributes, "Parameter", paramSuppressedWarnings).Contains("NoAlias"))
			symbol.IsNoAlias = true;

		return symbol;
	}

	private void ReportDeclarationDiagnostic(SyntaxNode node, string message)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.Report(currentFileContext, node.Span, message);
	}

	private void ReportDeclarationWarning(SyntaxNode node, string message, string id)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.ReportWarning(currentFileContext, node.Span, message, id);
	}

	private void DeclareExternFunction(ExternDeclarationSyntax ext)
	{
		context.SymbolUnits[ext.Name] = context.CurrentUnit!;
		var returnType = context.ResolveType(ext.ReturnType);
		if (returnType is null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, ext.Span, $"Unknown return type '{ext.ReturnType}'");
			return;
		}

		var parameters = new List<ParameterSymbol>();
		foreach (var param in ext.Parameters)
		{
			var paramType = context.ResolveType(param.Type);
			if (paramType is null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, param.Span, $"Unknown parameter type '{param.Type}'");
				continue;
			}

			parameters.Add(new ParameterSymbol(param.Name, paramType));
		}

		var existing = context.Globals.Lookup(ext.Name);
		if (existing is not null)
		{
			// If the existing symbol is also an extern, we can safely ignore the duplicate declaration
			if (existing is FunctionSymbol existingFunc && existingFunc.IsExtern)
			{
				return;
			}

			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, ext.Span, $"Duplicate definition of '{ext.Name}'");
			return;
		}

		// Declare the extern symbol with its unmangled base name
		var newSymbol = new FunctionSymbol(ext.Name, returnType, parameters, isExtern: true, isVariadic: ext.IsVariadic);
		context.Globals.Declare(newSymbol);

		// Keep candidates registered for lookup under the unmangled name
		if (!context.OverloadedFunctions.TryGetValue(ext.Name, out var candidates))
		{
			candidates = [];
			context.OverloadedFunctions[ext.Name] = candidates;
		}
		candidates.Add(newSymbol);
	}

	private void DeclareExtension(ExtensionDeclarationSyntax extDecl)
	{
		var extendedType = context.ResolveType(extDecl.ExtendedTypeName);
		if (extendedType is null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, extDecl.Span, $"Unknown type '{extDecl.ExtendedTypeName}' inside extension block.");
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
			templates.Add(extDecl);
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
			foreach (var method in extDecl.Methods)
				defaults.Add((method.Name, method));
		}

		// RETROACTIVE CONFORMANCE: "extension T : IName" records that the
		// extended concrete type conforms to the named interface and validates
		// that this extension provides every required method with a matching
		// signature. (Generic conformance `extension Pair<T> : IFoo` is deferred.)
		if (extDecl.ConformsTo is not null)
			RegisterConformance(extDecl, extendedType);

		// Destructors register as ordinary extension methods named "~T" (void, this-only)
		foreach (var method in extDecl.Methods.Concat(extDecl.Destructors.Select(static d => d.ToFunctionDeclaration())))
		{
			if (method.Name.StartsWith('~') && method.Name[1..] != extDecl.ExtendedTypeName)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, method.Span, $"Destructor name '{method.Name}' does not match extended type '{extDecl.ExtendedTypeName}'.");
				continue;
			}

			if (method.Name.StartsWith('~') && context.Destructors.ContainsKey(extDecl.ExtendedTypeName))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, method.Span, $"Duplicate destructor definition for type '{extDecl.ExtendedTypeName}'.");
				continue;
			}

			// Mangled name represents the scoped path, e.g., "MyNamespace.Point.Move"
			var baseMangledName = context.GetMangledName($"{extDecl.ExtendedTypeName}.{method.Name}", context.CurrentNamespace);

			// 1. Inject the implicit first parameter: "this"
			// It starts as a read-only pointer. The ValidationPass will upgrade it to mutable if needed!
			var thisParamType = new PointerTypeSymbol(extendedType, isMutable: false);
			var thisParam = new ParameterSymbol("this", thisParamType);

			var parameters = new List<ParameterSymbol> { thisParam };
			foreach (var param in method.Parameters)
			{
				var paramSymbol = CreateParameter(param);
				if (paramSymbol is null)
				{
					ReportDeclarationDiagnostic(param, $"Unknown parameter type '{param.Type}'");
					continue;
				}

				parameters.Add(paramSymbol);
			}

			var returnType = context.ResolveType(method.ReturnType);
			if (returnType is null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, method.Span, $"Unknown return type '{method.ReturnType}'");
				return;
			}

			// 2. Register the overloaded, parameter-mangled global signature
			var overloadedName = context.GetOverloadedMangledName(baseMangledName, parameters.Select(p => p.Type).ToList());

			var newSymbol = new FunctionSymbol(overloadedName, returnType, parameters);
			var methodSuppressedWarnings = new List<string>();
			ApplyFunctionAttributes(
				VerifyAttributes(method.Attributes, method.Name.StartsWith('~') ? "Destructor" : "Method", methodSuppressedWarnings),
				newSymbol,
				methodSuppressedWarnings);
			WarnIfUnsafeBodyUnused(method.Span, method.Body, newSymbol, methodSuppressedWarnings);
			context.Globals.Declare(newSymbol);

			if (!context.OverloadedFunctions.TryGetValue(baseMangledName, out var candidates))
			{
				candidates = [];
				context.OverloadedFunctions[baseMangledName] = candidates;
			}
			candidates.Add(newSymbol);

			context.SymbolUnits[overloadedName] = context.CurrentUnit!;

			if (method.Name.StartsWith('~'))
			{
				context.Destructors[extDecl.ExtendedTypeName] = newSymbol;
			}
		}

		foreach (var ctorDecl in extDecl.Constructors)
		{
			if (ctorDecl.StructName != extDecl.ExtendedTypeName)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, ctorDecl.Span, $"Constructor name '{ctorDecl.StructName}' must match the extended type '{extDecl.ExtendedTypeName}'.");
				continue;
			}

			if (extendedType is not StructTypeSymbol ctorStructType)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, ctorDecl.Span, $"Cannot define a constructor for non-struct type '{extDecl.ExtendedTypeName}'.");
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
				var paramSymbol = CreateParameter(param);
				if (paramSymbol is null)
				{
					ReportDeclarationDiagnostic(param, $"Unknown parameter type '{param.Type}'");
					hasBadParam = true;
					continue;
				}

				ctorParameters.Add(paramSymbol);
			}

			if (hasBadParam)
				continue;

			// 2. Register under the struct's name so 'T(...)' call sites resolve via existing overload machinery
			var ctorOverloadedName = context.GetOverloadedMangledName(ctorBaseMangledName, ctorParameters.Select(p => p.Type).ToList());
			var ctorSymbol = new FunctionSymbol(ctorOverloadedName, ctorStructType, ctorParameters);
			var ctorSuppressedWarnings = new List<string>();
			ApplyFunctionAttributes(VerifyAttributes(ctorDecl.Attributes, "Constructor", ctorSuppressedWarnings), ctorSymbol, ctorSuppressedWarnings);
			WarnIfUnsafeBodyUnused(ctorDecl.Span, ctorDecl.Body, ctorSymbol, ctorSuppressedWarnings);
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

	private void RegisterConformance(ExtensionDeclarationSyntax extDecl, TypeSymbol extendedType)
	{
		// Resolve the interface within the extension's declaration context.
		var prevUnit = context.CurrentUnit;
		var prevNs = context.CurrentNamespace;
		context.CurrentUnit = context.SymbolUnits.TryGetValue(extendedType.Name, out var extUnit) ? extUnit : context.CurrentUnit;
		context.CurrentNamespace = context.CurrentUnit?.NamespaceDeclaration?.Name;

		var interfaceType = context.ResolveType(extDecl.ConformsTo!);
		context.CurrentUnit = prevUnit;
		context.CurrentNamespace = prevNs;

		if (interfaceType is not InterfaceTypeSymbol interfaceSymbol)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, extDecl.Span, $"Unknown interface '{extDecl.ConformsTo}' in conformance declaration.");
			return;
		}

		// Record conformance: concrete type -> interface.
		if (!context.Conformance.TryGetValue(extendedType.Name, out var interfaces))
		{
			interfaces = [];
			context.Conformance[extendedType.Name] = interfaces;
		}
		interfaces.Add(interfaceSymbol.Name);

		// Validate the extension provides every required interface member.
		var interfaceDecl = context.InterfaceTemplates[interfaceSymbol.Name];
		var providedMethods = new HashSet<(string Name, string ReturnType, string Params)>();
		foreach (var method in extDecl.Methods)
			providedMethods.Add((method.Name, method.ReturnType, ParamsSignature(method.Parameters)));

		foreach (var member in interfaceDecl.Members)
		{
			var requiredSig = (member.Name, member.ReturnType, ParamsSignature(member.Parameters));
			if (!providedMethods.Contains(requiredSig))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, extDecl.Span,
					$"Type '{extDecl.ExtendedTypeName}' does not implement member '{RequiredSigText(member)}' required by interface '{extDecl.ConformsTo}'.");
			}
		}
	}

	private static string ParamsSignature(IReadOnlyList<ParameterSyntax> parameters)
		=> string.Join(",", parameters.Select(p => p.Type));

	private static string RequiredSigText(InterfaceMethodDeclarationSyntax member)
		=> $"{member.ReturnType} {member.Name}({ParamsSignature(member.Parameters)})";

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

		if (context.Globals.Lookup(globalDecl.Name) is not null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span, $"Duplicate definition of global variable '{globalDecl.Name}'.");
			return;
		}

		if (!IsCompileTimeConstant(globalDecl.Initializer))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span, $"Global variable '{globalDecl.Name}' must be initialized with a compile-time constant.");
			return;
		}

		// ref/refvar globals are inherently re-assignable (mutable references)
		var isRefType = globalDecl.Type is not null && globalDecl.Type.StartsWith("ref");
		var symbol = new VariableSymbol(globalDecl.Name, type, isMutable: globalDecl.IsMutable || isRefType)
		{
			IsInitialized = true,
			IsGlobal = true,
			Origin = OriginKind.Global
		};
		context.Globals.Declare(symbol);
		context.GlobalVariables.Add((globalDecl, symbol));
	}

	private static bool IsCompileTimeConstant(ExpressionSyntax? expr)
	{
		if (expr is null)
			return true; // zero-initialized

		return expr switch
		{
			IntegerLiteralExpressionSyntax or DoubleLiteralExpressionSyntax or BooleanLiteralExpressionSyntax or CharacterLiteralExpressionSyntax or NullLiteralExpressionSyntax => true,
			UnaryExpressionSyntax { Operator: "-" } unary => IsCompileTimeConstant(unary.Operand),
			StructInitializationExpressionSyntax structInit => structInit.Initializers.All(static m => IsCompileTimeConstant(m.Expression)),
			_ => false
		};
	}

	private void DeclareUnion(UnionDeclarationSyntax unionDecl)
	{
		var mangledName = context.GetMangledName(unionDecl.Name, context.CurrentNamespace);

		if (context.UnionTypes.ContainsKey(mangledName) || context.StructTypes.ContainsKey(mangledName) || TypeSymbol.FromName(unionDecl.Name) is not null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, unionDecl.Span, $"Duplicate type definition '{unionDecl.Name}'");
			return;
		}

		if (unionDecl.GenericParameters.Count > 0)
		{
			context.SymbolUnits[mangledName] = context.CurrentUnit!;
			context.GenericUnionTemplates[mangledName] = unionDecl;

			var placeholderFields = new List<UnionFieldSymbol>();
			var templateSymbol = new UnionTypeSymbol(mangledName, placeholderFields);
			context.UnionTypes[mangledName] = templateSymbol;
			return;
		}

		context.SymbolUnits[mangledName] = context.CurrentUnit!;
		var fields = new List<UnionFieldSymbol>();
		var fieldNames = new HashSet<string>();

		foreach (var field in unionDecl.Fields)
		{
			if (!fieldNames.Add(field.Name))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, field.Span, $"Duplicate field '{field.Name}' in union '{unionDecl.Name}'");
				continue;
			}

			TypeSymbol fieldType;
			var isVoidVariant = field.Type == "void";
			if (isVoidVariant)
			{
				fieldType = TypeSymbol.Void;
			}
			else
			{
				fieldType = context.ResolveType(field.Type ?? "");
				if (fieldType is null)
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, field.Span, $"Unknown type '{field.Type}' of field '{field.Name}' in union '{unionDecl.Name}'");
					continue;
				}
			}

			fields.Add(new UnionFieldSymbol(field.Name, fieldType, isVoidVariant));
		}

		var unionSymbol = new UnionTypeSymbol(mangledName, fields);
		context.UnionTypes[mangledName] = unionSymbol;
	}
}
