using Cvolo.Analysis.Builtins;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Declaration;

internal sealed class BuiltinValidator(BindingContext context)
{
	private static readonly BuiltinId[] RequiredBuiltins =
	[
		BuiltinId.SizeOf,
		BuiltinId.AlignOf,
		BuiltinId.OffsetOf,
		BuiltinId.Type,
		BuiltinId.UnsafeBodyAttribute,
		BuiltinId.NoAliasAttribute,
		BuiltinId.SuppressWarningAttribute,
		BuiltinId.FlagsAttribute,
		BuiltinId.NonExhaustiveAttribute,
		BuiltinId.StrictMutabilityAttribute,
		BuiltinId.IntrinsicAttribute,
		BuiltinId.MustUseAttribute,
		BuiltinId.ErrorAttribute,
		BuiltinId.ResultAttribute,
		BuiltinId.InlineAttribute,
		BuiltinId.NeverInlineAttribute,
		BuiltinId.LibraryImportAttribute,
		BuiltinId.ImportNameAttribute,
		BuiltinId.ExposeNameAttribute
	];

	public void Validate(IEnumerable<CompilationUnitSyntax> units)
	{
		var declared = new Dictionary<BuiltinId, SyntaxNode>();
		var baseUnitSeen = false;
		CompilationUnitSyntax? firstBaseUnit = null;

		foreach (var unit in units)
		{
			var origin = ResolveOrigin(unit);

			if (origin == SourceOrigin.BaseSdk)
			{
				baseUnitSeen = true;
				firstBaseUnit ??= unit;
			}

			var members = unit.NamespaceDeclaration != null ? unit.NamespaceDeclaration.Members : unit.Members;
			foreach (var member in members)
			{
				switch (member)
				{
					case FunctionDeclarationSyntax function:
						ValidateFunction(unit, origin, function, declared);
						break;
					case ConstructorDeclarationSyntax constructor:
						ValidateConstructor(unit, origin, constructor, declared);
						break;
					case DestructorDeclarationSyntax destructor:
						ValidateDestructor(unit, origin, destructor, declared);
						break;
					case StructDeclarationSyntax structDeclaration:
						ValidateType(unit, origin, structDeclaration, structDeclaration.IsBuiltin, structDeclaration.Name, BuiltinDeclarationKind.Struct, declared);
						break;
					case UnionDeclarationSyntax unionDeclaration:
						ValidateType(unit, origin, unionDeclaration, unionDeclaration.IsBuiltin, unionDeclaration.Name, BuiltinDeclarationKind.Union, declared);
						break;
					case EnumDeclarationSyntax enumDeclaration:
						ValidateType(unit, origin, enumDeclaration, enumDeclaration.IsBuiltin, enumDeclaration.Name, BuiltinDeclarationKind.Enum, declared);
						break;
					case ExtensionDeclarationSyntax extension:
						ValidateExtension(unit, origin, extension, declared);
						break;
				}
			}
		}

		if (baseUnitSeen && firstBaseUnit is not null)
		{
			var fileContext = context.FileContexts[firstBaseUnit];
			foreach (var required in RequiredBuiltins)
			{
				if (!declared.ContainsKey(required))
				{
					var entry = BuiltinCatalog.All.First(e => e.Id == required);
					context.Diagnostics.Report(fileContext, new TextSpan(0, 0),
						$"The Base SDK is missing the required builtin declaration '{entry.Name}'.",
						DiagnosticIds.RequiredBuiltinMissing);
				}
			}
		}
	}

	private SourceOrigin ResolveOrigin(CompilationUnitSyntax unit)
	{
		if (!context.FileContexts.TryGetValue(unit, out var fileContext))
			return SourceOrigin.Project;

		if (fileContext.Origin == SourceOrigin.BaseSdk)
			return SourceOrigin.BaseSdk;

		if (context.BaseSourcePaths.Count > 0 && context.BaseSourcePaths.Contains(Path.GetFullPath(fileContext.FilePath)))
			return SourceOrigin.BaseSdk;

		return fileContext.Origin;
	}

	private void ValidateExtension(CompilationUnitSyntax unit, SourceOrigin origin, ExtensionDeclarationSyntax extension, Dictionary<BuiltinId, SyntaxNode> declared)
	{
		foreach (var method in extension.Methods)
			ValidateFunction(unit, origin, method, declared);

		foreach (var constructor in extension.Constructors)
			ValidateConstructor(unit, origin, constructor, declared);

		foreach (var destructor in extension.Destructors)
			ValidateDestructor(unit, origin, destructor, declared);
	}

	private void ValidateFunction(CompilationUnitSyntax unit, SourceOrigin origin, FunctionDeclarationSyntax function, Dictionary<BuiltinId, SyntaxNode> declared)
	{
		if (!function.IsBuiltin)
			return;

		var fileContext = context.FileContexts[unit];

		if (origin != SourceOrigin.BaseSdk)
		{
			context.Diagnostics.Report(fileContext, function.Span,
				"'builtin' declarations are restricted to the Base SDK; project, System, and package source cannot declare builtins.",
				DiagnosticIds.BuiltinOutsideBase);
			return;
		}

		if (function.HasBody)
		{
			context.Diagnostics.Report(fileContext, function.NameSpan,
				$"A 'builtin' callable must be declaration-only and cannot declare a body.",
				DiagnosticIds.BuiltinCallableHasBody);
		}

		Bind(fileContext, function, function.Name, BuiltinDeclarationKind.Function, declared);
	}

	private void ValidateConstructor(CompilationUnitSyntax unit, SourceOrigin origin, ConstructorDeclarationSyntax constructor, Dictionary<BuiltinId, SyntaxNode> declared)
	{
		var fileContext = context.FileContexts[unit];

		if (!constructor.IsBuiltin)
		{
			if (constructor.IsDeclarationOnly)
			{
				context.Diagnostics.Report(fileContext, constructor.Span,
					$"Constructor '{constructor.StructName}' must declare a body unless it is marked 'builtin'.",
					DiagnosticIds.OrdinaryCallableBodyless);
			}

			return;
		}

		if (origin != SourceOrigin.BaseSdk)
		{
			context.Diagnostics.Report(fileContext, constructor.Span,
				"'builtin' declarations are restricted to the Base SDK; project, System, and package source cannot declare builtins.",
				DiagnosticIds.BuiltinOutsideBase);
			return;
		}

		Bind(fileContext, constructor, constructor.StructName, BuiltinDeclarationKind.Function, declared);
	}

	private void ValidateDestructor(CompilationUnitSyntax unit, SourceOrigin origin, DestructorDeclarationSyntax destructor, Dictionary<BuiltinId, SyntaxNode> declared)
	{
		var fileContext = context.FileContexts[unit];

		if (!destructor.IsBuiltin)
		{
			if (destructor.IsDeclarationOnly)
			{
				context.Diagnostics.Report(fileContext, destructor.Span,
					$"Destructor '~{destructor.StructName}' must declare a body unless it is marked 'builtin'.",
					DiagnosticIds.OrdinaryCallableBodyless);
			}

			return;
		}

		if (origin != SourceOrigin.BaseSdk)
		{
			context.Diagnostics.Report(fileContext, destructor.Span,
				"'builtin' declarations are restricted to the Base SDK; project, System, and package source cannot declare builtins.",
				DiagnosticIds.BuiltinOutsideBase);
			return;
		}

		Bind(fileContext, destructor, destructor.StructName, BuiltinDeclarationKind.Function, declared);
	}

	private void ValidateType(CompilationUnitSyntax unit, SourceOrigin origin, SyntaxNode declaration, bool isBuiltin, string name, BuiltinDeclarationKind kind, Dictionary<BuiltinId, SyntaxNode> declared)
	{
		if (!isBuiltin)
			return;

		var fileContext = context.FileContexts[unit];

		if (origin != SourceOrigin.BaseSdk)
		{
			context.Diagnostics.Report(fileContext, declaration.Span,
				"'builtin' declarations are restricted to the Base SDK; project, System, and package source cannot declare builtins.",
				DiagnosticIds.BuiltinOutsideBase);
			return;
		}

		if (Bind(fileContext, declaration, name, kind, declared) is { Policy: BuiltinImplementationPolicy.CompileTimeOnly })
		{
			context.CompileTimeOnlyBuiltinTypes.Add(name);
		}
	}

	private BuiltinEntry? Bind(CompilationContext fileContext, SyntaxNode declaration, string name, BuiltinDeclarationKind kind, Dictionary<BuiltinId, SyntaxNode> declared)
	{
		if (!BuiltinCatalog.TryGet(name, out var entry))
		{
			context.Diagnostics.Report(fileContext, declaration.Span,
				$"Unknown builtin declaration '{name}'.",
				DiagnosticIds.UnknownBuiltinDeclaration);
			return null;
		}

		if (entry.Kind != kind)
		{
			context.Diagnostics.Report(fileContext, declaration.Span,
				$"Builtin '{name}' is declared with the wrong shape; expected a {entry.Kind.ToString().ToLowerInvariant()} declaration.",
				DiagnosticIds.BuiltinShapeMismatch);
			return null;
		}

		if (declared.TryGetValue(entry.Id, out _))
		{
			context.Diagnostics.Report(fileContext, declaration.Span,
				$"Builtin '{name}' is declared more than once in the Base SDK.",
				DiagnosticIds.DuplicateBuiltinDeclaration);
			return null;
		}

		declared[entry.Id] = declaration;
		context.BuiltinBindings[declaration] = entry.Id;
		return entry;
	}
}
