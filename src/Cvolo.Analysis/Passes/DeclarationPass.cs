using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;

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
		_ = VerifyAttributes(structDecl.Attributes, "Struct");

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

				var instDecl = new FunctionDeclarationSyntax(func.Span, func.ReturnType, instName, [], func.Parameters, func.Body);
				context.MonomorphizedFunctionDecls.Add(instDecl);
				return;
			}

			// Record the original template file unit
			context.SymbolUnits[mangledName] = context.CurrentUnit!;

			context.GenericFunctionTemplates[mangledName] = func;
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

		var newSymbol = new FunctionSymbol(overloadedMangledName, type, parameters);
		ApplyFunctionAttributes(VerifyAttributes(func.Attributes, "Function"), newSymbol);
		context.Globals.Declare(newSymbol);

		if (!context.OverloadedFunctions.TryGetValue(mangledName, out var candidates))
		{
			candidates = [];
			context.OverloadedFunctions[mangledName] = candidates;
		}

		candidates.Add(newSymbol);
	}

	// M1 attribute model: only System.* intrinsics exist. Their [AttributeUsage]-style rules
	// (syntactic target x safety context, per spec section 4) are modeled compiler-side until
	// the language has enums/inheritance to declare them in source. Attributes are erased
	// before emission - they never reach LLVM IR.
	private static readonly Dictionary<string, (string[] Targets, string[] Contexts)> IntrinsicAttributes = new()
	{
		["UnsafeBody"] = (["Function", "Method", "Constructor", "Destructor"], ["Safe", "Unbound", "Unsafe"]),
		["NoAlias"] = (["Function", "Method", "Parameter"], ["Unbound", "Unsafe"])
	};

	private static string? NormalizeAttributeName(string attributeName)
	{
		var simple = attributeName.Contains('.') ? attributeName[(attributeName.LastIndexOf('.') + 1)..] : attributeName;
		if (simple.EndsWith("Attribute", StringComparison.Ordinal))
			simple = simple[..^"Attribute".Length];

		return IntrinsicAttributes.ContainsKey(simple) ? simple : null;
	}

	/// <summary>Verifies each attribute against its syntactic attach point and returns the canonical keys that passed.</summary>
	private List<string> VerifyAttributes(IReadOnlyList<AttributeSyntax> attributes, string syntacticTarget)
	{
		var applied = new List<string>();
		foreach (var attr in attributes)
		{
			var key = NormalizeAttributeName(attr.Name);
			if (key is null)
			{
				ReportDeclarationDiagnostic(attr, $"Unknown attribute '{attr.Name}'.");
				continue;
			}

			var (targets, contexts) = IntrinsicAttributes[key];
			if (!targets.Contains(syntacticTarget))
			{
				ReportDeclarationDiagnostic(attr, $"Attribute '[{key}]' cannot be applied to {syntacticTarget.ToLowerInvariant()} declarations.");
				continue;
			}

			if (!contexts.Contains("Safe"))
			{
				ReportDeclarationDiagnostic(attr, $"Attribute '[{key}]' can only be applied inside Unbound or Unsafe contexts.");
				continue;
			}

			applied.Add(key);
		}

		return applied;
	}

	private static void ApplyFunctionAttributes(List<string> appliedKeys, FunctionSymbol symbol)
	{
		if (appliedKeys.Contains("UnsafeBody"))
			symbol.IsUnsafeBody = true;

		if (appliedKeys.Contains("NoAlias"))
			symbol.IsNoAlias = true;
	}

	/// <summary>Resolves a parameter's type, verifies its attributes, and returns null when the type is unknown.</summary>
	private ParameterSymbol? CreateParameter(ParameterSyntax param)
	{
		var paramType = context.ResolveType(param.Type);
		if (paramType is null)
			return null;

		var symbol = new ParameterSymbol(param.Name, paramType);
		if (VerifyAttributes(param.Attributes, "Parameter").Contains("NoAlias"))
			symbol.IsNoAlias = true;

		return symbol;
	}

	private void ReportDeclarationDiagnostic(SyntaxNode node, string message)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.Report(currentFileContext, node.Span, message);
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
			ApplyFunctionAttributes(
				VerifyAttributes(method.Attributes, method.Name.StartsWith('~') ? "Destructor" : "Method"),
				newSymbol);
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
			ApplyFunctionAttributes(VerifyAttributes(ctorDecl.Attributes, "Constructor"), ctorSymbol);
			context.Globals.Declare(ctorSymbol);

			if (!context.OverloadedFunctions.TryGetValue(ctorBaseMangledName, out var ctorCandidates))
			{
				ctorCandidates = [];
				context.OverloadedFunctions[ctorBaseMangledName] = ctorCandidates;
			}
			ctorCandidates.Add(ctorSymbol);

			context.SymbolUnits[ctorOverloadedName] = context.CurrentUnit!;

			if (!context.Constructors.TryGetValue(extDecl.ExtendedTypeName, out var registeredCtors))
			{
				registeredCtors = [];
				context.Constructors[extDecl.ExtendedTypeName] = registeredCtors;
			}
			registeredCtors.Add(ctorSymbol);
		}
	}

	private void DeclareGlobalVariable(GlobalVariableDeclarationSyntax globalDecl)
	{
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

		var symbol = new VariableSymbol(globalDecl.Name, type, isMutable: globalDecl.IsMutable)
		{
			IsInitialized = true,
			IsGlobal = true
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
}
