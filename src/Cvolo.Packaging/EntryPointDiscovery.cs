using Cvolo.Analysis;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;

namespace Cvolo.Packaging;

public static class EntryPointDiscovery
{
	public static bool HasEntryPoint(BindingContext context, IEnumerable<CompilationUnitSyntax> units)
	{
		if (context.Globals.Lookup("main") is not null || context.Globals.Lookup("Main") is not null)
			return true;

		return units.Any(ContainsEntryPointDeclaration);
	}

	private static bool ContainsEntryPointDeclaration(CompilationUnitSyntax unit)
	{
		if (unit.Members.Any(IsEntryPointDeclaration))
			return true;

		return unit.NamespaceDeclaration?.Members.Any(IsEntryPointDeclaration) == true;
	}

	private static bool IsEntryPointDeclaration(SyntaxNode member)
	{
		return member is FunctionDeclarationSyntax function
			&& (function.Name == "main" || function.Name == "Main")
			&& function.Parameters.Count == 0
			&& function.GenericParameters.Count == 0
			&& function.Receiver == ReceiverContract.None
			&& function.BindingKind == FunctionBindingKind.Default;
	}
}
