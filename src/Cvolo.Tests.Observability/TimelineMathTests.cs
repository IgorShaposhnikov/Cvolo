using Cvolo.Observability;

namespace Cvolo.Tests.Observability;

/// <summary>Exclusive time, overlapping spans and critical path without double counting.</summary>
public class TimelineMathTests
{
	[Fact]
	public void Exclusive_time_subtracts_union_of_overlapping_children_once()
	{
		// parent: 0..1000; children overlap: 100..400 and 300..600 → union = 100..600 = 500
		var parent = new OperationInterval("parent", null, 0, 1000, "c1");
		var childA = new OperationInterval("childA", "parent", 100, 400, "c1");
		var childB = new OperationInterval("childB", "parent", 300, 600, "c1");
		var all = new List<OperationInterval> { parent, childA, childB };

		var exclusive = TimelineAnalysis.ExclusiveTicks(parent, all, out var ignored);

		Assert.Equal(500, exclusive); // 1000 − 500 union; overlap counted once
		Assert.Equal(0, ignored);
	}

	[Fact]
	public void Children_on_other_clock_domains_are_never_subtracted()
	{
		var parent = new OperationInterval("parent", null, 0, 1000, "c1");
		var foreignChild = new OperationInterval("remote", "parent", 100, 400, "c2");
		var all = new List<OperationInterval> { parent, foreignChild };

		var exclusive = TimelineAnalysis.ExclusiveTicks(parent, all, out var ignored);

		Assert.Equal(1000, exclusive); // no cross-clock subtraction (§61)
		Assert.Equal(1, ignored);
	}

	[Fact]
	public void Parallel_non_overlapping_siblings_subtract_completely()
	{
		var parent = new OperationInterval("root", null, 0, 1000, "c1");
		var left = new OperationInterval("l", "root", 0, 500, "c1");
		var right = new OperationInterval("r", "root", 500, 1000, "c1");
		var all = new List<OperationInterval> { parent, left, right };

		Assert.Equal(0, TimelineAnalysis.ExclusiveTicks(parent, all));
	}

	[Fact]
	public void Children_outside_parent_span_do_not_shrink_exclusive_time()
	{
		var parent = new OperationInterval("root", null, 500, 1000, "c1");
		var earlier = new OperationInterval("before", "root", 0, 400, "c1");
		var later = new OperationInterval("after", "root", 1200, 1400, "c1");
		var all = new List<OperationInterval> { parent, earlier, later };

		Assert.Equal(500, TimelineAnalysis.ExclusiveTicks(parent, all));
	}

	[Fact]
	public void Critical_path_follows_longest_child_chain_not_wall_clock_guess()
	{
		var root = new OperationInterval("root", null, 0, 1000, "c1");
		var fast = new OperationInterval("fast", "root", 0, 300, "c1");
		var slow = new OperationInterval("slow", "root", 0, 800, "c1");
		var deepest = new OperationInterval("deepest", "slow", 100, 700, "c1");
		var all = new List<OperationInterval> { root, fast, slow, deepest };

		var path = TimelineAnalysis.CriticalPath(all);

		Assert.Equal(new[] { "root", "slow", "deepest" }, path);
	}

	[Fact]
	public void Unattributed_parents_are_counted_not_invented()
	{
		var orphan = new OperationInterval("orphan", "ghost", 0, 10, "c1");
		var root = new OperationInterval("root", null, 0, 10, "c1");
		var child = new OperationInterval("child", "root", 0, 5, "c1");

		Assert.Equal(1, TimelineAnalysis.UnattributedCount([orphan, root, child]));
	}
}
