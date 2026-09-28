using Cvolo.Compiler.Tooling;

using AnalysisLayout = Cvolo.Analysis.Layout;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Maps the compiler's internal layout inspection onto the tooling DTO. The numeric facts are
/// copied verbatim, so the editor can never disagree with <c>sizeof</c>/<c>alignof</c>/<c>offsetof</c>
/// or with the layout CodeLens, which is produced from the same inspection.
/// </summary>
internal static class TypeLayoutAdapter
{
	public static TypeLayoutInspection ToTooling(AnalysisLayout.TypeLayoutInspection? inspection)
	{
		if (inspection is null)
			return null!;

		var members = new List<TypeLayoutMemberInspection>(inspection.Members.Count);

		foreach (var member in inspection.Members)
			members.Add(new TypeLayoutMemberInspection(member.Name, member.TypeDisplay, member.Offset, member.Size, member.Alignment));

		var padding = new List<TypeLayoutPaddingInspection>(inspection.Padding.Count);

		foreach (var region in inspection.Padding)
			padding.Add(new TypeLayoutPaddingInspection(region.Offset, region.Size, ToTooling(region.Kind)));

		return new TypeLayoutInspection(
			inspection.TypeDisplay,
			inspection.TargetDisplay,
			inspection.Size,
			inspection.Alignment,
			inspection.PayloadSize,
			inspection.PaddingSize,
			inspection.Stride,
			inspection.ElementCount,
			inspection.ElementSize,
			inspection.ElementAlignment,
			members,
			padding);
	}

	private static ToolingPaddingKind ToTooling(AnalysisLayout.TypeLayoutPaddingKind kind) => kind switch
	{
		AnalysisLayout.TypeLayoutPaddingKind.Tail => ToolingPaddingKind.Tail,
		_ => ToolingPaddingKind.Internal,
	};
}
