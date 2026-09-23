using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.FFI;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Declaration;

internal sealed class NativeAbiPostDeclarationValidator(BindingContext context)
{
	public void Validate()
	{
		foreach (var u in context.UnionTypes.Values.Where(static u => u.IsUnsafe))
		{
			if (NativeAbiSemanticSafety.ContainsResourceBearingValue(context, u))
			{
				Report(u, $"Raw unsafe union '{u.Name}' contains a resource-bearing value.", DiagnosticIds.UnsafeUnionFieldNotAbiSafe);
			}
		}

		foreach (var d in context.DelegateTypes.Values.Where(static d => d.IsNative))
		{
			if (Contains(d.ReturnType, d.Parameters))
			{
				Report(d, $"Native delegate '{d.Name}' contains a resource-bearing type.", DiagnosticIds.NativeAbiResourceBearing);
			}
		}

		foreach (var f in context.OverloadedFunctions.Values.SelectMany(static x => x).Where(static f => f.IsExtern || f.IsNativeAbi || f.IsExported))
		{
			if (Contains(f.ReturnType, f.Parameters))
			{
				Report(f, $"Native ABI function '{f.Name}' contains a resource-bearing type.", DiagnosticIds.NativeAbiResourceBearing);
			}
		}

		foreach (var (_, g) in context.GlobalVariables)
		{
			if (g.IsForeign && NativeAbiSemanticSafety.ContainsResourceBearingValue(context, g.Type))
			{
				Report(g, $"Foreign global '{g.Name}' uses a resource-bearing type.", DiagnosticIds.NativeAbiResourceBearing);
			}
		}
	}

	private bool Contains(TypeSymbol ret, IReadOnlyList<ParameterSymbol> ps)
	{
		return NativeAbiSemanticSafety.ContainsResourceBearingValue(context, ret) || ps.Any(p => NativeAbiSemanticSafety.ContainsResourceBearingValue(context, p.Type));
	}

	private void Report(TypeSymbol type, string message, string id)
	{
		if (context.SymbolUnits.TryGetValue(type.Name, out var unit) && context.FileContexts.TryGetValue(unit, out var fileContext))
		{
			context.Diagnostics.Report(fileContext, unit.Span, message, id);
		}
	}

	private void Report(Symbol symbol, string message, string id)
	{
		var unit = symbol.DeclaringUnit;
		if (unit is null)
			context.SymbolUnits.TryGetValue(symbol.Name, out unit);
		if (unit is not null && context.FileContexts.TryGetValue(unit, out var fileContext))
			context.Diagnostics.Report(fileContext, unit.Span, message, id);
	}
}
