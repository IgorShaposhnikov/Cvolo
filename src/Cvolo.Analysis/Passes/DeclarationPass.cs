using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using static System.Net.Mime.MediaTypeNames;

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
			var paramType = context.ResolveType(param.Type);
			if (paramType is null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, param.Span, $"Unknown parameter type '{param.Type}'");
				continue;
			}

			parameters.Add(new ParameterSymbol(param.Name, paramType));
		}

		var existing = context.Globals.Lookup(mangledName);
		if (existing is not null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, func.Span, $"Duplicate definition of '{func.Name}'");
			return;
		}

		context.Globals.Declare(new FunctionSymbol(mangledName, type, parameters));
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
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, ext.Span, $"Duplicate definition of '{ext.Name}'");
			return;
		}

		context.Globals.Declare(new FunctionSymbol(ext.Name, returnType, parameters, isExtern: true, isVariadic: ext.IsVariadic));
	}
}
