using Cvolo.Analysis.Ownership;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;

namespace Cvolo.Analysis.Passes;

/// <summary>
/// Pass 5: binds flow-sensitive ownership decisions for assignments whose destination may hold an
/// owned resource-move value. Runs after the safety pass so borrow/lifetime facts are available.
/// </summary>
public sealed class OwnershipPass(BindingContext context)
{
	private readonly OwnershipAnalyzer _analyzer = new(context);

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
					_analyzer.Analyze(func);
				}
				else if (member is ExposeExternBlockSyntax exportBlock)
				{
					foreach (var exportFunc in exportBlock.Functions)
					{
						if (exportFunc.GenericParameters.Count == 0 && !exportFunc.Name.Contains('<'))
							_analyzer.Analyze(exportFunc);
					}
				}
				else if (member is ExtensionDeclarationSyntax extDecl)
				{
					if (context.GenericStructTemplates.ContainsKey(extDecl.ExtendedTypeName) ||
						context.GenericUnionTemplates.ContainsKey(extDecl.ExtendedTypeName))
					{
						continue;
					}

					foreach (var method in extDecl.Methods
						.Concat(extDecl.Destructors.Select(static d => d.ToFunctionDeclaration())))
					{
						_analyzer.Analyze(method);
					}

					foreach (var ctor in extDecl.Constructors)
					{
						_analyzer.Analyze(ctor.ToFunctionDeclaration());
					}
				}
			}
		}

		foreach (var instDecl in context.MonomorphizedFunctionDecls)
		{
			_analyzer.Analyze(instDecl);
		}

		foreach (var decl in context.MonomorphizedExtensionDecls)
		{
			if (decl is FunctionDeclarationSyntax func)
			{
				_analyzer.Analyze(func);
			}
			else if (decl is ConstructorDeclarationSyntax ctor)
			{
				_analyzer.Analyze(ctor.ToFunctionDeclaration());
			}
		}
	}
}
