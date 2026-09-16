using System.Reflection;
using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

public sealed class BoundaryTests
{
	private static readonly string[] ForbiddenAssemblies =
	[
		"Cvolo.Core",
		"Cvolo.Syntax",
		"Cvolo.Syntax.Antlr",
		"Cvolo.Analysis",
		"Cvolo.Packaging",
		"Antlr4.Runtime"
	];

	[Fact]
	public void PublicApi_DoesNotLeakCompilerInternalTypes()
	{
		var assembly = typeof(CvoloWorkspace).Assembly;
		var violations = new List<string>();

		foreach (var type in assembly.GetExportedTypes())
		{
			CheckType(type.BaseType, $"base of {type.FullName}", violations);

			foreach (var interfaceType in type.GetInterfaces())
				CheckType(interfaceType, $"interface of {type.FullName}", violations);

			const BindingFlags flags =
				BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

			foreach (var ctor in type.GetConstructors(flags))
				CheckMethod(ctor, violations);

			foreach (var property in type.GetProperties(flags))
				CheckType(property.PropertyType, $"property {type.Name}.{property.Name}", violations);

			foreach (var field in type.GetFields(flags))
				CheckType(field.FieldType, $"field {type.Name}.{field.Name}", violations);

			foreach (var method in type.GetMethods(flags).Where(m => !m.IsSpecialName))
				CheckMethod(method, violations);

			foreach (var evt in type.GetEvents(flags))
				CheckType(evt.EventHandlerType, $"event {type.Name}.{evt.Name}", violations);
		}

		Assert.True(
			violations.Count == 0,
			$"Cvolo.Compiler.Tooling public API references compiler-internal types:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
	}

	private static void CheckMethod(MethodBase method, List<string> violations)
	{
		foreach (var parameter in method.GetParameters())
			CheckType(parameter.ParameterType, $"parameter of {method.Name}", violations);

		if (method is MethodInfo methodInfo)
			CheckType(methodInfo.ReturnType, $"return of {method.Name}", violations);

		if (method.IsGenericMethodDefinition)
		{
			foreach (var genericArgument in method.GetGenericArguments())
				CheckType(genericArgument, $"generic argument of {method.Name}", violations);
		}
	}

	private static void CheckType(Type? type, string context, List<string> violations)
	{
		if (type is null)
			return;

		if (type.IsByRef || type.IsPointer || type.IsArray)
		{
			CheckType(type.GetElementType(), context, violations);
			return;
		}

		var assemblyName = type.Assembly.GetName().Name;
		if (ForbiddenAssemblies.Contains(assemblyName))
		{
			violations.Add($"{context}: {type.FullName} from {assemblyName}");
			return;
		}

		if (!type.IsGenericType)
			return;

		foreach (var genericArgument in type.GetGenericArguments())
			CheckType(genericArgument, $"{context} (generic argument)", violations);
	}

	[Fact]
	public void WorkspaceApi_ExposesOnlyToolingOwnedTypes()
	{
		var workspace = typeof(CvoloWorkspace);

		Assert.Equal("Cvolo.Compiler.Tooling", workspace.Namespace);
		Assert.Equal("Cvolo.Compiler.Tooling", typeof(CvoloProject).Namespace);
		Assert.Equal("Cvolo.Compiler.Tooling", typeof(ProjectSnapshot).Namespace);
		Assert.Equal("Cvolo.Compiler.Tooling", typeof(DocumentSnapshot).Namespace);
		Assert.Equal("Cvolo.Compiler.Tooling", typeof(Diagnostic).Namespace);
		Assert.Equal("Cvolo.Compiler.Tooling", typeof(SourceText).Namespace);
	}

	[Fact]
	public void CompilerInternals_AreNotExportedFromToolingAssembly()
	{
		var assembly = typeof(CvoloWorkspace).Assembly;

		var leaked = assembly.GetExportedTypes()
			.Where(t => t.Namespace?.StartsWith("Cvolo.Core", StringComparison.Ordinal) == true
				|| t.Namespace?.StartsWith("Cvolo.Syntax", StringComparison.Ordinal) == true
				|| t.Namespace?.StartsWith("Cvolo.Analysis", StringComparison.Ordinal) == true)
			.Select(t => t.FullName)
			.ToArray();

		Assert.Empty(leaked);
	}
}
