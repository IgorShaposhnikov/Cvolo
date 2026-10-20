namespace Cvolo.Observability;

/// <summary>
/// One closed operation on a single clock domain (§61): intervals from different
/// clock domains are never subtracted from each other.
/// </summary>
public readonly record struct OperationInterval(
	string OperationId,
	string? ParentOperationId,
	long StartTicks,
	long EndTicks,
	string? ClockId)
{
	public long DurationTicks => Math.Max(0, EndTicks - StartTicks);
}

/// <summary>
/// Timeline math (§8, §61, OBS-CORE-007): exclusive time subtracts the *union* of direct
/// children intervals (overlapping siblings never double-count), unattributed time is
/// reported instead of silently assigned, and the critical path follows real parent/child
/// edges — not wall-clock ordering guesses.
/// </summary>
public static class TimelineAnalysis
{
	/// <summary>
	/// Exclusive ticks of <paramref name="span"/>: its duration minus the union of direct
	/// children on the same clock domain, clamped at 0. Children on other clocks or with
	/// invalid edges are ignored for subtraction and reported via <paramref name="ignoredChildren"/>.
	/// </summary>
	public static long ExclusiveTicks(in OperationInterval span, IReadOnlyList<OperationInterval> all, out int ignoredChildren)
	{
		ArgumentNullException.ThrowIfNull(all);
		ignoredChildren = 0;
		var children = new List<(long Start, long End)>();

		foreach (var candidate in all)
		{
			if (!string.Equals(candidate.ParentOperationId, span.OperationId, StringComparison.Ordinal))
			{
				continue;
			}

			if (candidate.ClockId is not null && span.ClockId is not null
				&& !string.Equals(candidate.ClockId, span.ClockId, StringComparison.Ordinal))
			{
				ignoredChildren++; // never subtract across clock domains (§61)
				continue;
			}

			if (candidate.EndTicks <= span.StartTicks || candidate.StartTicks >= span.EndTicks)
			{
				continue; // no overlap with parent
			}

			children.Add((Math.Max(candidate.StartTicks, span.StartTicks), Math.Min(candidate.EndTicks, span.EndTicks)));
		}

		return Math.Max(0, span.DurationTicks - UnionLength(children));
	}

	public static long ExclusiveTicks(in OperationInterval span, IReadOnlyList<OperationInterval> all)
		=> ExclusiveTicks(span, all, out _);

	/// <summary>Intervals whose parent id does not exist in the set (must stay flagged, not guessed, §61).</summary>
	public static int UnattributedCount(IReadOnlyList<OperationInterval> all)
	{
		ArgumentNullException.ThrowIfNull(all);
		var ids = new HashSet<string>(all.Select(i => i.OperationId), StringComparer.Ordinal);
		var count = 0;
		foreach (var interval in all)
		{
			if (interval.ParentOperationId is { } parent && !ids.Contains(parent))
			{
				count++;
			}
		}
		return count;
	}

	/// <summary>
	/// Critical path root→leaf: repeatedly follows the child with the latest reachable end
	/// within each parent (ties broken by longer duration, then id), per clock domain of the
	/// starting root. Returns operation ids in path order.
	/// </summary>
	public static IReadOnlyList<string> CriticalPath(IReadOnlyList<OperationInterval> all)
	{
		ArgumentNullException.ThrowIfNull(all);
		if (all.Count == 0)
		{
			return [];
		}

		var children = new Dictionary<string, List<OperationInterval>>(StringComparer.Ordinal);
		var byId = new Dictionary<string, OperationInterval>(StringComparer.Ordinal);
		foreach (var interval in all)
		{
			byId[interval.OperationId] = interval;
			if (interval.ParentOperationId is { } parent)
			{
				if (!children.TryGetValue(parent, out var list))
				{
					children[parent] = list = [];
				}
				list.Add(interval);
			}
		}

		// Roots = intervals without a resolvable parent inside the set.
		var roots = all
			.Where(i => i.ParentOperationId is null || !byId.ContainsKey(i.ParentOperationId))
			.OrderBy(i => i.StartTicks)
			.ToList();

		List<string>? best = null;
		long bestEnd = long.MinValue;
		foreach (var root in roots)
		{
			var chain = LongestChain(root, children);
			var end = byId[chain[^1]].EndTicks;
			if (best is null || end > bestEnd || (end == bestEnd && chain.Count > best.Count))
			{
				best = chain;
				bestEnd = end;
			}
		}

		return best ?? [];
	}

	private static List<string> LongestChain(in OperationInterval root, Dictionary<string, List<OperationInterval>> children)
	{
		var chain = new List<string> { root.OperationId };
		var current = root;
		while (children.TryGetValue(current.OperationId, out var kids) && kids.Count > 0)
		{
			var next = kids
				.OrderByDescending(k => k.EndTicks)
				.ThenByDescending(k => k.DurationTicks)
				.ThenBy(k => k.OperationId, StringComparer.Ordinal)
				.First();
			chain.Add(next.OperationId);
			current = next;
		}
		return chain;
	}

	private static long UnionLength(List<(long Start, long End)> spans)
	{
		if (spans.Count <= 1)
		{
			return spans.Count == 1 ? spans[0].End - spans[0].Start : 0;
		}

		spans.Sort((a, b) => a.Start.CompareTo(b.Start));
		long total = 0;
		var (curStart, curEnd) = spans[0];
		for (var i = 1; i < spans.Count; i++)
		{
			var (start, end) = spans[i];
			if (start > curEnd)
			{
				total += curEnd - curStart;
				curStart = start;
				curEnd = end;
			}
			else if (end > curEnd)
			{
				curEnd = end;
			}
		}
		total += curEnd - curStart;
		return total;
	}
}
