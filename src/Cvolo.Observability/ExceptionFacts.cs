using System.Text;

namespace Cvolo.Observability;

/// <summary>
/// Structured exception facts for events (§31.1: exceptions carry stack+chain on allowed
/// levels). Produces already-materialized attribute pairs — observers never format
/// exceptions lazily after an IsEnabled gate (§39).
/// </summary>
public static class ExceptionFacts
{
	/// <summary>
	/// Flattens the exception inner chain into <c>exception.type</c> / <c>exception.message</c>
	/// (outermost first, joined with " -> " / "; "), plus <c>exception.stack</c> when
	/// <paramref name="includeStack"/> is requested by the caller's recording level (§25).
	/// </summary>
	public static IReadOnlyList<KeyValuePair<string, string>> Describe(Exception exception, bool includeStack = false)
	{
		ArgumentNullException.ThrowIfNull(exception);

		var types = new StringBuilder();
		var messages = new StringBuilder();
		StringBuilder? stack = includeStack ? new StringBuilder() : null;
		var depth = 0;

		for (Exception? current = exception; current is not null; current = current.InnerException, depth++)
		{
			if (depth > 0)
			{
				types.Append(" -> ");
				messages.Append("; ");
				stack?.AppendLine().Append("--- inner: ").AppendLine(current.GetType().FullName);
			}

			types.Append(current.GetType().FullName);
			messages.Append(current.Message);
			stack?.Append(current.StackTrace);
		}

		var result = new List<KeyValuePair<string, string>>(3)
		{
			new("exception.type", types.ToString()),
			new("exception.message", messages.ToString()),
		};
		if (stack is not null)
		{
			result.Add(new("exception.stack", stack.ToString()));
		}
		return result;
	}

	/// <summary>Shorthand: type + message only (no stack — the default for basic/detailed, §25).</summary>
	public static IReadOnlyList<KeyValuePair<string, string>> DescribeShallow(Exception exception)
		=> Describe(exception, includeStack: false);
}
