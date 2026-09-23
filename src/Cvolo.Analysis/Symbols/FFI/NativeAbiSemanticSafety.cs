using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;

namespace Cvolo.Analysis.Symbols.FFI;

public static class NativeAbiSemanticSafety
{
	public static bool ContainsResourceBearingValue(TypeSymbol type)
	{
		return ContainsStatic(type, []);
	}

	public static bool ContainsResourceBearingValue(BindingContext context, TypeSymbol type)
	{
		return Contains(context, type, []);
	}

	private static bool Contains(BindingContext context, TypeSymbol type, HashSet<TypeSymbol> active)
	{
		switch (type)
		{
			case ArrayTypeSymbol a:
				return Contains(context, a.ElementType, active);
			case StructTypeSymbol s:
				if (!active.Add(s))
				{
					return false;
				}

				try
				{
					if (s.HasDestructor || context.Destructors.ContainsKey(s.Name))
					{
						return true;
					}

					return s.EmbeddedType is not null && Contains(context, s.EmbeddedType, active) || s.Fields.Any(f => Contains(context, f.Type, active));
				}
				finally
				{
					active.Remove(s);
				}
			case UnionTypeSymbol { IsUnsafe: true } u:
				if (!active.Add(u))
				{
					return false;
				}

				try
				{
					return u.Fields.Where(f => !f.IsVoidVariant).Any(f => Contains(context, f.Type, active));
				}
				finally
				{
					active.Remove(u);
				}
			default:
				return false;
		}
	}

	private static bool ContainsStatic(TypeSymbol type, HashSet<TypeSymbol> active)
	{
		switch (type)
		{
			case ArrayTypeSymbol a: return ContainsStatic(a.ElementType, active);
			case StructTypeSymbol s:
				if (!active.Add(s))
				{
					return false;
				}

				try
				{
					return s.HasDestructor || (s.EmbeddedType is not null && ContainsStatic(s.EmbeddedType, active)) || s.Fields.Any(f => ContainsStatic(f.Type, active));
				}
				finally
				{
					active.Remove(s);
				}
			case UnionTypeSymbol { IsUnsafe: true } u:
				if (!active.Add(u))
				{
					return false;
				}

				try
				{
					return u.Fields.Where(f => !f.IsVoidVariant).Any(f => ContainsStatic(f.Type, active));
				}
				finally
				{
					active.Remove(u);
				}
			default:
				return false;
		}
	}
}
