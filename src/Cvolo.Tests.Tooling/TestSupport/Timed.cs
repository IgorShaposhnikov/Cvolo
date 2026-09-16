namespace Cvolo.Tests.Tooling;

internal static class Timed
{
	public static void Out(Action body, int seconds = 15)
	{
		Out(body, TimeSpan.FromSeconds(seconds));
	}

	public static void Out(Action body, TimeSpan timeout)
	{
		var task = Task.Run(body);
		if (!task.Wait(timeout))
		{
			throw new TimeoutException($"Test exceeded the {timeout.TotalSeconds:0}s execution limit.");
		}

		task.GetAwaiter().GetResult();
	}
}
