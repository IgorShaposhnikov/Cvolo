using Cvolo.Analysis.Passes.Safety;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;

namespace Cvolo.Analysis.Passes;

public sealed class SafetyPass(BindingContext context)
{
	private SafetyAnalysisServices? _services;

	/// <summary>
	/// Lazily creates the safety-analysis service graph used by this pass instance.
	/// </summary>
	private SafetyAnalysisServices Services => _services ??= new SafetyAnalysisServices(context);

	public void Process(IEnumerable<CompilationUnitSyntax> units)
	{
		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			context.CurrentNamespace = unit.NamespaceDeclaration?.Name;
			var members = context.CurrentNamespace != null ? unit.NamespaceDeclaration!.Members : unit.Members;

			foreach (var member in members)
			{
				if (member is FunctionDeclarationSyntax func && func.GenericParameters.Count == 0 && !func.Name.Contains('<'))
				{
					Services.FunctionSafety.Check(func);
				}
				else if (member is ExposeExternBlockSyntax exportBlock)
				{
					foreach (var exportFunc in exportBlock.Functions)
					{
						if (exportFunc.GenericParameters.Count == 0 && !exportFunc.Name.Contains('<'))
							Services.FunctionSafety.Check(exportFunc);
					}
				}
				else if (member is ExtensionDeclarationSyntax extDecl)
				{
					// Skip generic templates; their monomorphized concrete instances are checked below
					if (context.GenericStructTemplates.ContainsKey(extDecl.ExtendedTypeName) ||
						context.GenericUnionTemplates.ContainsKey(extDecl.ExtendedTypeName))
					{
						continue;
					}

					foreach (var method in extDecl.Methods
						.Concat(extDecl.Destructors.Select(static d => d.ToFunctionDeclaration())))
					{
						Services.FunctionSafety.Check(method);
					}

					foreach (var ctor in extDecl.Constructors)
					{
						Services.FunctionSafety.Check(ctor.ToFunctionDeclaration());
					}
				}
			}
		}

		// Enforce safety pass on all monomorphized generic functions and extension methods!
		foreach (var instDecl in context.MonomorphizedFunctionDecls)
		{
			Services.FunctionSafety.Check(instDecl);
		}

		foreach (var decl in context.MonomorphizedExtensionDecls)
		{
			if (decl is FunctionDeclarationSyntax func)
			{
				Services.FunctionSafety.Check(func);
			}
			else if (decl is ConstructorDeclarationSyntax ctor)
			{
				Services.FunctionSafety.Check(ctor.ToFunctionDeclaration());
			}
		}
	}
}
