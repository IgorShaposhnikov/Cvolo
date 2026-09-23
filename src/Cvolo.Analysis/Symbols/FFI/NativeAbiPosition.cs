namespace Cvolo.Analysis.Symbols.FFI;

/// <summary>
/// The position at which a type crosses the C ABI boundary. Rules differ per position:
/// fixed arrays are rejected as direct parameters/returns but allowed inline in aggregates,
/// and <c>string</c> is allowed only as the existing extern import bridge.
/// </summary>
public enum NativeAbiPosition
{
	/// <summary>Function parameter of a native-ABI function, exposed export, or native delegate.</summary>
	Parameter,

	/// <summary>Function return type of a native-ABI function, exposed export, or native delegate.</summary>
	Return,

	/// <summary>Storage type of an imported foreign global.</summary>
	ForeignGlobalStorage,

	/// <summary>Field of a struct that crosses the C ABI</summary>
	AggregateField,

	/// <summary>Field of an 'unsafe union'.</summary>
	RawUnionField,

	/// <summary>Parameter of an imported extern function inside an extern block (the existing bridge).</summary>
	ImportedExternParameter,
}