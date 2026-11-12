// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

namespace Files.App.Utils
{
	internal static class PhotoLayoutPlanner
	{
		internal readonly record struct Region(double X, double Y, double Width, double Height);

		private sealed class Node(int index, double aspect, Node? first = null, Node? second = null, bool horizontal = true)
		{
			internal int Index { get; } = index;
			internal double Aspect { get; } = aspect;
			internal Node? First { get; } = first;
			internal Node? Second { get; } = second;
			internal bool Horizontal { get; } = horizontal;
			internal double MinimumArea { get; init; } = 1;
		}

		internal static Region[] Calculate(IReadOnlyList<double> aspects, double screenAspect)
		{
			if (aspects.Count == 0) return [];
			if (!double.IsFinite(screenAspect) || screenAspect <= 0) throw new ArgumentOutOfRangeException(nameof(screenAspect));
			var leaves = aspects.Select((aspect, index) => new Node(index, double.IsFinite(aspect) && aspect > 0 ? aspect : 1)).ToArray();
			Node? best = null;
			var bestScore = -1d;
			void Consider(Node node)
			{
				var coverage = Math.Min(node.Aspect / screenAspect, screenAspect / node.Aspect);
				var fairness = Math.Sqrt(Math.Clamp(node.MinimumArea * aspects.Count, 0, 1));
				var score = coverage * (0.92 + 0.08 * fairness);
				if (score <= bestScore) return;
				best = node;
				bestScore = score;
			}
			var sorted = leaves.OrderBy(node => node.Aspect).ToArray();
			foreach (var order in new[] { leaves, sorted })
			{
				if (leaves.Length <= 24)
					foreach (var candidate in Search(order, screenAspect)) Consider(candidate);
				var maxRows = Math.Min(leaves.Length, Math.Max(1, (int)Math.Ceiling(Math.Sqrt(leaves.Length)) * 3));
				for (var rows = 1; rows <= maxRows; rows++)
				{
					var bands = Enumerable.Range(0, rows).Select(_ => new List<Node>()).ToArray();
					for (var i = 0; i < order.Length; i++) bands[i * rows / order.Length].Add(order[i]);
					Consider(Join(bands.Select(band => Join(band, 0, band.Count, true)).ToArray(), 0, rows, false));
				}
			}
			// Keep frames proportional to their crops while maximizing useful screen area.
			var result = new Region[aspects.Count];
			var width = Math.Min(1, best!.Aspect / screenAspect);
			var height = Math.Min(1, screenAspect / best.Aspect);
			Place(best, new((1 - width) / 2, (1 - height) / 2, width, height), result);
			for (var columns = 1; columns <= aspects.Count; columns++)
			{
				var rows = (aspects.Count + columns - 1) / columns;
				var candidate = new Region[aspects.Count];
				for (var i = 0; i < candidate.Length; i++)
				{
					var frameWidth = Math.Min(1d / columns, leaves[i].Aspect / (rows * screenAspect));
					var frameHeight = frameWidth * screenAspect / leaves[i].Aspect;
					candidate[i] = new((i % columns + 0.5) / columns - frameWidth / 2, (i / columns + 0.5) / rows - frameHeight / 2, frameWidth, frameHeight);
				}
				var usedArea = candidate.Sum(region => region.Width * region.Height);
				var fairness = Math.Sqrt(Math.Clamp(candidate.Min(region => region.Width * region.Height) * aspects.Count / usedArea, 0, 1));
				var score = usedArea * (0.92 + 0.08 * fairness);
				if (score <= bestScore) continue;
				bestScore = score;
				result = candidate;
			}
			return result;
		}

		private static Node Combine(Node first, Node second, bool horizontal)
		{
			var aspect = horizontal ? first.Aspect + second.Aspect : 1 / (1 / first.Aspect + 1 / second.Aspect);
			var fraction = horizontal ? first.Aspect / aspect : aspect / first.Aspect;
			return new(-1, aspect, first, second, horizontal) { MinimumArea = Math.Min(first.MinimumArea * fraction, second.MinimumArea * (1 - fraction)) };
		}

		private static Node Join(IReadOnlyList<Node> nodes, int start, int count, bool horizontal)
		{
			if (count == 1) return nodes[start];
			var split = count / 2;
			return Combine(Join(nodes, start, split, horizontal), Join(nodes, start + split, count - split, horizontal), horizontal);
		}

		private static Node[] Search(Node[] leaves, double target)
		{
			var count = leaves.Length;
			var table = new Node[count, count + 1][];
			for (var i = 0; i < count; i++) table[i, 1] = [leaves[i]];
			for (var length = 2; length <= count; length++)
			{
				for (var start = 0; start + length <= count; start++)
				{
					var buckets = new Dictionary<int, Node>();
					for (var split = 1; split < length; split++)
						foreach (var first in table[start, split])
							foreach (var second in table[start + split, length - split])
								for (var orientation = 0; orientation < 2; orientation++)
								{
									var horizontal = orientation == 0;
									var aspect = horizontal ? first.Aspect + second.Aspect : 1 / (1 / first.Aspect + 1 / second.Aspect);
									var fraction = horizontal ? first.Aspect / aspect : aspect / first.Aspect;
									var minimum = Math.Min(first.MinimumArea * fraction, second.MinimumArea * (1 - fraction));
									var bucket = (int)Math.Round(Math.Log(aspect) * 40);
									if (!buckets.TryGetValue(bucket, out var existing) || minimum > existing.MinimumArea)
										buckets[bucket] = new(-1, aspect, first, second, horizontal) { MinimumArea = minimum };
								}
					var ordered = buckets.Values.OrderBy(node => node.Aspect).ToArray();
					if (length == count || ordered.Length <= 16) table[start, length] = ordered;
					else
					{
						// Retain varied aspect ratios while bounding the search for larger subsets.
						var selected = Enumerable.Range(0, 16).Select(i => ordered[(int)Math.Round(i * (ordered.Length - 1) / 15d)]).ToList();
						selected.Add(ordered.MinBy(node => Math.Abs(Math.Log(node.Aspect / target)))!);
						table[start, length] = selected.Distinct().ToArray();
					}
				}
			}
			return table[0, count];
		}

		private static void Place(Node node, Region area, Region[] result)
		{
			if (node.Index >= 0) { result[node.Index] = area; return; }
			var first = node.First!;
			var second = node.Second!;
			if (node.Horizontal)
			{
				var width = area.Width * first.Aspect / node.Aspect;
				Place(first, new(area.X, area.Y, width, area.Height), result);
				Place(second, new(area.X + width, area.Y, area.Width - width, area.Height), result);
			}
			else
			{
				var height = area.Height * node.Aspect / first.Aspect;
				Place(first, new(area.X, area.Y, area.Width, height), result);
				Place(second, new(area.X, area.Y + height, area.Width, area.Height - height), result);
			}
		}
	}
}
