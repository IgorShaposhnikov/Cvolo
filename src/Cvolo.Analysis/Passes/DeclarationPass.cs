using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;

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
		// 1. Calculate the fully qualified mangled name (e.g., App.Math.Point)
		var mangledName = context.GetMangledName(structDecl.Name, context.CurrentNamespace);

		if (context.StructTypes.ContainsKey(mangledName) || TypeSymbol.FromName(structDecl.Name) is not null)
		{
			context.Diagnostics.Report(structDecl.Span, $"Duplicate type definition '{structDecl.Name}'");
			return;
		}

		var fields = new List<StructFieldSymbol>();
		var fieldNames = new HashSet<string>();

		foreach (var field in structDecl.Fields)
		{
			if (!fieldNames.Add(field.Name))
			{
				context.Diagnostics.Report(field.Span, $"Duplicate field '{field.Name}' in struct '{structDecl.Name}'");
				continue;
			}

			var fieldType = context.ResolveType(field.Type);
			if (fieldType is null)
			{
				context.Diagnostics.Report(field.Span, $"Unknown type '{field.Type}' of field '{field.Name}'");
				continue;
			}

			fields.Add(new StructFieldSymbol(field.Name, fieldType));
		}

		// 2. Create the symbol and register it under the mangled name (Added mangledName here)
		var structSymbol = new StructTypeSymbol(mangledName, fields);
		context.StructTypes[mangledName] = structSymbol;
	}

	private void DeclareFunction(FunctionDeclarationSyntax func)
	{
		var type = context.ResolveType(func.ReturnType);
		if (type is null)
		{
			context.Diagnostics.Report(func.Span, $"Unknown return type '{func.ReturnType}'");
			return;
		}

		var parameters = new List<ParameterSymbol>();
		foreach (var param in func.Parameters)
		{
			var paramType = context.ResolveType(param.Type);
			if (paramType is null)
			{
				context.Diagnostics.Report(param.Span, $"Unknown parameter type '{param.Type}'");
				continue;
			}

			parameters.Add(new ParameterSymbol(param.Name, paramType));
		}

		var existing = context.Globals.Lookup(func.Name);
		if (existing is not null)
		{
			context.Diagnostics.Report(func.Span, $"Duplicate definition of '{func.Name}'");
			return;
		}

		context.Globals.Declare(new FunctionSymbol(func.Name, type, parameters));
	}

	private void DeclareExternFunction(ExternDeclarationSyntax ext)
	{
		var returnType = context.ResolveType(ext.ReturnType);
		if (returnType is null)
		{
			context.Diagnostics.Report(ext.Span, $"Unknown return type '{ext.ReturnType}'");
			return;
		}

		var parameters = new List<ParameterSymbol>();
		foreach (var param in ext.Parameters)
		{
			var paramType = context.ResolveType(param.Type);
			if (paramType is null)
			{
				context.Diagnostics.Report(param.Span, $"Unknown parameter type '{param.Type}'");
				continue;
			}

			parameters.Add(new ParameterSymbol(param.Name, paramType));
		}

		var existing = context.Globals.Lookup(ext.Name);
		if (existing is not null)
		{
			context.Diagnostics.Report(ext.Span, $"Duplicate definition of '{ext.Name}'");
			return;
		}

		context.Globals.Declare(new FunctionSymbol(ext.Name, returnType, parameters, isExtern: true, isVariadic: ext.IsVariadic));
	}
}
