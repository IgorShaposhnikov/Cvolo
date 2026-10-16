using Cvolo.Analysis.Symbols.Base;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;

namespace Cvolo.Analysis.Passes.Validation;

public enum FormationContextKind
{
    General,
    Embedded
}

public sealed class EmbeddedFormationContext(string path) : IFormationContext
{
    public FormationContextKind Kind => FormationContextKind.Embedded;
    public string Path { get; } = path;
}

public sealed class GeneralFormationContext : IFormationContext
{
    public FormationContextKind Kind => FormationContextKind.General;
    public static readonly GeneralFormationContext Instance = new();
}

public interface IFormationContext
{
    FormationContextKind Kind { get; }
}

public enum DefaultabilityState
{
    Unknown,
    Eligible,
    Ineligible,
    Unverified
}

public sealed class DefaultPlan
{
    public required TypeSymbol Type { get; init; }
    public required IFormationContext Context { get; init; }
    public required bool IsEligible { get; init; }
    public string? Reason { get; init; }
    public DefaultabilityState State { get; init; } = DefaultabilityState.Unknown;
}

/// <summary>
/// A packaging-neutral view of a producer-emitted type-level defaultability proof,
/// registered against an imported type's mangled name. The state is one of <c>Eligible</c>,
/// <c>Ineligible</c> or <c>Unverified</c>; the fingerprint binds the proof to the serialized
/// type shape so a stale/mismatched certificate is not honored.
/// </summary>
public sealed record ImportedDefaultCertificate(string State, string? Reason, string? Fingerprint)
{
    public const string Eligible = "Eligible";
    public const string Ineligible = "Ineligible";
    public const string Unverified = "Unverified";
}

/// <summary>
/// Canonical type-shape fingerprints shared by the package producer (Sector 1 metadata)
/// and the consumer (rehydrated declarations). Both sides derive the same digest from the same
/// field/attribute shape, so a certificate that no longer matches its metadata is detected.
/// </summary>
public static class DefaultabilityFingerprint
{
    public static string Struct(string name, string? embeddedType, IEnumerable<(string Type, string Name, string Visibility)> fields, IEnumerable<string> attributes)
        => Compute("struct", name, embeddedType ?? string.Empty, fields, attributes);

    public static string Union(string name, bool isUnsafe, IEnumerable<(string Type, string Name, string Visibility)> fields, IEnumerable<string> attributes)
        => Compute(isUnsafe ? "union:unsafe" : "union:safe", name, string.Empty, fields, attributes);

    private static string Compute(string kind, string name, string embeddedType, IEnumerable<(string Type, string Name, string Visibility)> fields, IEnumerable<string> attributes)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(kind).Append('|').Append(name).Append('|').Append(embeddedType).Append('|');
        foreach (var field in fields)
            sb.Append(field.Type).Append(':').Append(field.Name).Append(':').Append(field.Visibility).Append(';');
        sb.Append('|');
        foreach (var attribute in attributes)
            sb.Append(attribute).Append(',');
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes);
    }
}

/// <summary>
/// Resolves producer-emitted defaultability certificates for imported types from a binding
/// context. Shared by the local-expression and global-initializer validation paths so an imported
/// aggregate is judged identically wherever it appears.
/// </summary>
public static class ImportedDefaultCertificateResolver
{
    /// <summary>True when the type is declared in a package-API metadata unit (external package surface).</summary>
    public static bool IsImported(BindingContext context, TypeSymbol type)
    {
        var unit = GetDeclaringUnit(context, type);
        return unit is not null && context.ExternalPackageUnits.Contains(unit);
    }

    /// <summary>
    /// Consults the registered certificate for an imported type. Returns false when no certificate is
    /// registered (caller must fail closed). When true, <paramref name="eligible"/> is the honored
    /// verdict, <paramref name="reason"/> is the producer cause, and <paramref name="fingerprintMatches"/>
    /// is false when the proof no longer matches the serialized shape (treat as Unverified).
    /// </summary>
    public static bool TryResolve(BindingContext context, TypeSymbol type, out bool eligible, out string? reason, out bool fingerprintMatches)
    {
        eligible = false;
        reason = null;
        fingerprintMatches = false;
        if (!IsImported(context, type))
            return false;
        if (!context.ImportedDefaultCertificates.TryGetValue(type.Name, out var cert))
            return false;

        var actual = TryComputeFingerprint(context, type);
        fingerprintMatches = cert.Fingerprint is null || actual is null || string.Equals(cert.Fingerprint, actual, StringComparison.Ordinal);
        eligible = cert.State == ImportedDefaultCertificate.Eligible;
        reason = cert.Reason;
        return true;
    }

    /// <summary>Recomputes the canonical type-shape fingerprint from the rehydrated declaration.</summary>
    public static string? TryComputeFingerprint(BindingContext context, TypeSymbol type)
    {
        var unit = GetDeclaringUnit(context, type);
        if (unit is null)
            return null;
        var simpleName = type.Name.Contains('.') ? type.Name[(type.Name.LastIndexOf('.') + 1)..] : type.Name;
        var ns = unit.NamespaceDeclaration;
        var members = ns is null ? unit.Members : ns.Members;
        foreach (var structDecl in members.OfType<StructDeclarationSyntax>())
        {
            if (structDecl.Name == simpleName)
            {
                var fields = structDecl.Fields.Select(f => (f.Type, f.Name, f.Visibility.ToString()));
                return DefaultabilityFingerprint.Struct(structDecl.Name, structDecl.EmbeddedType, fields, ZeroArgumentAttributeNames(structDecl.Attributes));
            }
        }

        foreach (var unionDecl in members.OfType<UnionDeclarationSyntax>())
        {
            if (unionDecl.Name == simpleName)
            {
                var fields = unionDecl.Fields.Select(f => (f.Type, f.Name, f.Visibility.ToString()));
                return DefaultabilityFingerprint.Union(unionDecl.Name, unionDecl.IsUnsafe, fields, ZeroArgumentAttributeNames(unionDecl.Attributes));
            }
        }

        return null;
    }

    private static CompilationUnitSyntax? GetDeclaringUnit(BindingContext context, TypeSymbol type)
    {
        foreach (var name in ExpandTemplateNames(type))
        {
            if (context.SymbolUnits.TryGetValue(name, out var unit))
                return unit;
        }

        return null;
    }

    private static IEnumerable<string> ExpandTemplateNames(TypeSymbol type)
    {
        yield return type.Name;
        var lt = type.Name.IndexOf('<');
        if (lt > 0)
            yield return type.Name[..lt];
    }

    private static IEnumerable<string> ZeroArgumentAttributeNames(IEnumerable<AttributeSyntax> attributes) =>
        attributes.Where(attribute => attribute.Arguments.Count == 0).Select(attribute => attribute.Name);
}
