using Cvolo.Analysis;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Tests.Generics;

/// <summary>
/// Open generic names are bound while a template scope is active, but they are not concrete
/// runtime instantiations. Materializing them would enqueue synthetic bodies whose type parameters
/// are no longer in scope when the later monomorphized-validation queue runs.
/// </summary>
public sealed class OpenGenericTypeResolutionTests
{
	[Fact]
	public void OpenStructTypeArgument_ReturnsTemplateWithoutConcreteMaterialization()
	{
		var context = new BindingContext();
		var template = new StructDeclarationSyntax(
			new TextSpan(0, 0),
			"Box",
			new[] { "T" },
			Array.Empty<StructFieldSyntax>());
		var templateSymbol = new StructTypeSymbol("Box", []);

		context.GenericStructTemplates["Box"] = template;
		context.StructTypes["Box"] = templateSymbol;
		context.PushGenericScope("test", "Box", new[] { "T" });
		try
		{
			var resolved = context.ResolveType("Box<T>");

			Assert.Same(templateSymbol, resolved);
			Assert.DoesNotContain("Box<T>", context.StructTypes.Keys);
		}
		finally
		{
			context.PopGenericScope();
		}
	}

	[Fact]
	public void OpenUnionTypeArgument_ReturnsTemplateWithoutConcreteMaterialization()
	{
		var context = new BindingContext();
		var template = new UnionDeclarationSyntax(
			new TextSpan(0, 0),
			"Maybe",
			new[] { "T" },
			Array.Empty<UnionFieldSyntax>());
		var templateSymbol = new UnionTypeSymbol("Maybe", []);

		context.GenericUnionTemplates["Maybe"] = template;
		context.UnionTypes["Maybe"] = templateSymbol;
		context.PushGenericScope("test", "Maybe", new[] { "T" });
		try
		{
			var resolved = context.ResolveType("Maybe<T>");

			Assert.Same(templateSymbol, resolved);
			Assert.DoesNotContain("Maybe<T>", context.UnionTypes.Keys);
		}
		finally
		{
			context.PopGenericScope();
		}
	}
}
