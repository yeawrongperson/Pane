namespace Wallflow.Core;

public sealed record MonitorTopologyItem(
    string Key,
    double DesktopX,
    double DesktopY,
    double DisplayWidth,
    double DisplayHeight,
    MonitorVisualDescriptor Descriptor,
    bool IsConnected = true);

public sealed record MonitorTopologyPlacement(
    string Key,
    double X,
    double Y,
    double Width,
    double Height,
    double Scale,
    bool IsConnected);

public sealed record MonitorTopologyResult(
    IReadOnlyList<MonitorTopologyPlacement> Placements,
    double ContentWidth,
    double ContentHeight,
    double Scale);

/// <summary>Pure, deterministic desktop-topology projection shared by all Pane renderers.</summary>
public static class MonitorTopologyLayout
{
    public const double FallbackLandscapeWidth = 1920;
    public const double FallbackLandscapeHeight = 1080;
    public static readonly double ReferenceVisualDiagonal = Math.Sqrt(
        FallbackLandscapeWidth * FallbackLandscapeWidth +
        FallbackLandscapeHeight * FallbackLandscapeHeight);
    public const double TrustedDiagonalBaselineInches = 24;
    public const double MinimumTrustedSizeFactor = 0.80;
    public const double MaximumTrustedSizeFactor = 1.60;
    public const double AlignmentToleranceRatio = 0.10;
    public static readonly double UncompressedDesktopGap = ReferenceVisualDiagonal / 16;
    public static readonly double MaximumSemanticDesktopGap = ReferenceVisualDiagonal / 4;

    public static MonitorTopologyResult Calculate(
        IEnumerable<MonitorTopologyItem> source,
        double viewportWidth,
        double viewportHeight,
        double padding = 0,
        double gap = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        var items = source.Select(Normalize).ToArray();
        var safeWidth = FinitePositive(viewportWidth, 1);
        var safeHeight = FinitePositive(viewportHeight, 1);
        var safePadding = ClampFinite(padding, 0, Math.Min(safeWidth, safeHeight) / 2);
        var safeGap = ClampFinite(gap, 0, Math.Min(safeWidth, safeHeight) / 2);
        if (items.Length == 0) return new([], 0, 0, 1);

        var projectedX = ProjectAxis(items, horizontal: true, safeGap);
        var projectedY = ProjectAxis(items, horizontal: false, safeGap);
        var contentWidth = items.Max(item => projectedX[item.Index] + item.Width);
        var contentHeight = items.Max(item => projectedY[item.Index] + item.Height);
        var availableWidth = Math.Max(1, safeWidth - safePadding * 2);
        var availableHeight = Math.Max(1, safeHeight - safePadding * 2);
        var scale = Math.Min(availableWidth / Math.Max(1, contentWidth),
            availableHeight / Math.Max(1, contentHeight));
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        var renderedWidth = contentWidth * scale;
        var renderedHeight = contentHeight * scale;
        var offsetX = safePadding + (availableWidth - renderedWidth) / 2;
        var offsetY = safePadding + (availableHeight - renderedHeight) / 2;
        var placements = items.Select(item => new MonitorTopologyPlacement(
            item.Key,
            offsetX + projectedX[item.Index] * scale,
            offsetY + projectedY[item.Index] * scale,
            Math.Max(double.Epsilon, item.Width * scale),
            Math.Max(double.Epsilon, item.Height * scale),
            scale,
            item.Connected)).ToArray();
        return new(placements, renderedWidth, renderedHeight, scale);
    }

    /// <summary>
    /// Preserves ordinary desktop gaps, then smoothly compresses larger gaps toward a finite visual cap.
    /// </summary>
    public static double CompressDesktopGap(double rawGap)
    {
        var gap = Math.Max(0, Finite(rawGap));
        if (gap <= UncompressedDesktopGap) return gap;
        var excess = gap - UncompressedDesktopGap;
        var compressionRange = MaximumSemanticDesktopGap - UncompressedDesktopGap;
        return UncompressedDesktopGap + compressionRange * (excess / (excess + compressionRange));
    }

    public static double PhysicalSizeFactor(MonitorVisualDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.PhysicalSizeConfidence != PhysicalSizeConfidence.EdidReported ||
            descriptor.PhysicalDiagonalInches is not > 0 ||
            !double.IsFinite(descriptor.PhysicalDiagonalInches.Value)) return 1;
        return Math.Clamp(
            Math.Sqrt(descriptor.PhysicalDiagonalInches.Value / TrustedDiagonalBaselineInches),
            MinimumTrustedSizeFactor,
            MaximumTrustedSizeFactor);
    }

    private static NormalizedItem Normalize(MonitorTopologyItem item, int index)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(item.Descriptor);
        var portrait = item.Descriptor.Orientation is DisplayOrientation.Portrait or DisplayOrientation.PortraitFlipped;
        var fallbackWidth = portrait ? FallbackLandscapeHeight : FallbackLandscapeWidth;
        var fallbackHeight = portrait ? FallbackLandscapeWidth : FallbackLandscapeHeight;
        var displayWidth = FinitePositive(item.DisplayWidth, fallbackWidth);
        var displayHeight = FinitePositive(item.DisplayHeight, fallbackHeight);
        var aspectRatio = displayWidth / displayHeight;
        var visualDiagonal = ReferenceVisualDiagonal * PhysicalSizeFactor(item.Descriptor);
        var visualHeight = visualDiagonal / Math.Sqrt(aspectRatio * aspectRatio + 1);
        var visualWidth = aspectRatio * visualHeight;
        return new(index, item.Key ?? string.Empty, Finite(item.DesktopX), Finite(item.DesktopY),
            displayWidth, displayHeight, visualWidth, visualHeight, item.Descriptor, item.IsConnected);
    }

    private static double[] ProjectAxis(IReadOnlyList<NormalizedItem> items, bool horizontal, double visualGap)
    {
        var starts = items.Select(item => horizontal ? item.RawX : item.RawY).Distinct().Order().ToArray();
        var projectedStarts = new Dictionary<double, double> { [starts[0]] = 0 };
        for (var index = 1; index < starts.Length; index++)
            projectedStarts[starts[index]] = projectedStarts[starts[index - 1]] +
                CompressDesktopGap(PositiveDifference(starts[index], starts[index - 1]));

        var positions = new double[items.Count];
        foreach (var item in items)
            positions[item.Index] = projectedStarts[horizontal ? item.RawX : item.RawY];
        var alignmentGroups = ApplyStrongAlignments(items, positions, horizontal);
        EnforceEdgeSeparation(items, positions, alignmentGroups, horizontal, visualGap);

        var minimum = positions.Min();
        for (var index = 0; index < positions.Length; index++)
            positions[index] -= minimum;
        return positions;
    }

    private static int[] ApplyStrongAlignments(
        IReadOnlyList<NormalizedItem> items,
        double[] positions,
        bool horizontal)
    {
        var candidates = new List<AlignmentConstraint>();
        for (var firstIndex = 0; firstIndex < items.Count; firstIndex++)
        for (var secondIndex = firstIndex + 1; secondIndex < items.Count; secondIndex++)
        {
            var first = items[firstIndex];
            var second = items[secondIndex];
            if (!SeparatedOnOtherAxis(first, second, horizontal)) continue;
            var constraint = ClosestAlignment(first, second, horizontal);
            if (constraint is not null) candidates.Add(constraint);
        }

        var forest = new DisjointSet(items.Count);
        var edges = new List<AlignmentConstraint>();
        foreach (var candidate in candidates.OrderBy(candidate => candidate.Error).ThenBy(candidate => candidate.FirstIndex).ThenBy(candidate => candidate.SecondIndex))
        {
            if (!forest.Union(candidate.FirstIndex, candidate.SecondIndex)) continue;
            edges.Add(candidate);
        }

        var adjacency = Enumerable.Range(0, items.Count).ToDictionary(index => index, _ => new List<(int Index, double Delta)>());
        foreach (var edge in edges)
        {
            adjacency[edge.FirstIndex].Add((edge.SecondIndex, edge.SecondMinusFirst));
            adjacency[edge.SecondIndex].Add((edge.FirstIndex, -edge.SecondMinusFirst));
        }

        var visited = new bool[items.Count];
        for (var root = 0; root < items.Count; root++)
        {
            if (visited[root] || adjacency[root].Count == 0) continue;
            var offsets = new Dictionary<int, double> { [root] = 0 };
            var queue = new Queue<int>();
            queue.Enqueue(root);
            visited[root] = true;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var edge in adjacency[current])
                {
                    if (visited[edge.Index]) continue;
                    visited[edge.Index] = true;
                    offsets[edge.Index] = offsets[current] + edge.Delta;
                    queue.Enqueue(edge.Index);
                }
            }

            var anchor = offsets.Average(entry => positions[entry.Key] - entry.Value);
            foreach (var entry in offsets)
                positions[entry.Key] = anchor + entry.Value;
        }
        return Enumerable.Range(0, items.Count).Select(forest.Find).ToArray();
    }

    private static void EnforceEdgeSeparation(
        IReadOnlyList<NormalizedItem> items,
        double[] positions,
        int[] alignmentGroups,
        bool horizontal,
        double visualGap)
    {
        var ordered = items.OrderBy(item => horizontal ? item.RawX : item.RawY).ThenBy(item => item.Index).ToArray();
        var ranks = ordered.GroupBy(item => horizontal ? item.RawX : item.RawY).Select(group => group.ToArray()).ToArray();
        for (var pass = 0; pass < items.Count; pass++)
        {
            var changed = false;
            foreach (var item in ordered)
            foreach (var previous in ordered)
            {
                if (previous.Index == item.Index) break;
                var start = horizontal ? item.RawX : item.RawY;
                var previousStart = horizontal ? previous.RawX : previous.RawY;
                var previousLength = horizontal ? previous.RawWidth : previous.RawHeight;
                var previousEnd = SafeEnd(previousStart, previousLength);
                if (previousEnd > start || !ShouldSeparateOnAxis(previous, item, horizontal)) continue;

                var previousVisualLength = horizontal ? previous.Width : previous.Height;
                var required = positions[previous.Index] + previousVisualLength + visualGap +
                    CompressDesktopGap(PositiveDifference(start, previousEnd));
                var shift = required - positions[item.Index];
                if (shift <= 1e-9) continue;
                var group = alignmentGroups[item.Index];
                for (var index = 0; index < positions.Length; index++)
                    if (alignmentGroups[index] == group)
                        positions[index] += shift;
                changed = true;
            }
            for (var rankIndex = 1; rankIndex < ranks.Length; rankIndex++)
            {
                var previousRank = ranks[rankIndex - 1];
                foreach (var item in ranks[rankIndex])
                {
                    var group = alignmentGroups[item.Index];
                    var preceding = previousRank.Where(previous => alignmentGroups[previous.Index] != group).ToArray();
                    if (preceding.Length == 0) continue;
                    var required = preceding.Max(previous => positions[previous.Index]) + visualGap;
                    var shift = required - positions[item.Index];
                    if (shift <= 1e-9) continue;
                    for (var index = 0; index < positions.Length; index++)
                        if (alignmentGroups[index] == group)
                            positions[index] += shift;
                    changed = true;
                }
            }
            if (!changed) break;
        }
    }

    private static AlignmentConstraint? ClosestAlignment(
        NormalizedItem first,
        NormalizedItem second,
        bool horizontal)
    {
        var firstRawStart = horizontal ? first.RawX : first.RawY;
        var secondRawStart = horizontal ? second.RawX : second.RawY;
        var firstRawLength = horizontal ? first.RawWidth : first.RawHeight;
        var secondRawLength = horizontal ? second.RawWidth : second.RawHeight;
        var firstVisualLength = horizontal ? first.Width : first.Height;
        var secondVisualLength = horizontal ? second.Width : second.Height;
        var tolerance = Math.Min(firstRawLength, secondRawLength) * AlignmentToleranceRatio;
        var alignments = new[]
        {
            (Name: horizontal ? "Left" : "Top", Error: Math.Abs(firstRawStart - secondRawStart), Delta: 0d),
            (Name: horizontal ? "HorizontalCenter" : "VerticalCenter",
                Error: Math.Abs((firstRawStart + firstRawLength / 2) - (secondRawStart + secondRawLength / 2)),
                Delta: (firstVisualLength - secondVisualLength) / 2),
            (Name: horizontal ? "Right" : "Bottom",
                Error: Math.Abs(SafeEnd(firstRawStart, firstRawLength) - SafeEnd(secondRawStart, secondRawLength)),
                Delta: firstVisualLength - secondVisualLength)
        };
        var closest = alignments.OrderBy(alignment => alignment.Error).First();
        return closest.Error <= tolerance
            ? new(first.Index, second.Index, closest.Delta, closest.Error / Math.Max(1, tolerance), closest.Name)
            : null;
    }

    private static bool SeparatedOnOtherAxis(NormalizedItem first, NormalizedItem second, bool horizontal)
    {
        var firstStart = horizontal ? first.RawY : first.RawX;
        var firstLength = horizontal ? first.RawHeight : first.RawWidth;
        var secondStart = horizontal ? second.RawY : second.RawX;
        var secondLength = horizontal ? second.RawHeight : second.RawWidth;
        return SafeEnd(firstStart, firstLength) <= secondStart || SafeEnd(secondStart, secondLength) <= firstStart;
    }

    private static bool OverlapsOnOtherAxis(NormalizedItem first, NormalizedItem second, bool projectingHorizontally)
    {
        var firstStart = projectingHorizontally ? first.RawY : first.RawX;
        var firstLength = projectingHorizontally ? first.RawHeight : first.RawWidth;
        var secondStart = projectingHorizontally ? second.RawY : second.RawX;
        var secondLength = projectingHorizontally ? second.RawHeight : second.RawWidth;
        return firstStart < SafeEnd(secondStart, secondLength) && secondStart < SafeEnd(firstStart, firstLength);
    }

    private static bool ShouldSeparateOnAxis(NormalizedItem first, NormalizedItem second, bool horizontal)
    {
        if (OverlapsOnOtherAxis(first, second, horizontal)) return true;
        var axisGap = EdgeGap(first, second, horizontal);
        var otherAxisGap = EdgeGap(first, second, !horizontal);
        return axisGap <= otherAxisGap;
    }

    private static double EdgeGap(NormalizedItem first, NormalizedItem second, bool horizontal)
    {
        var firstStart = horizontal ? first.RawX : first.RawY;
        var firstLength = horizontal ? first.RawWidth : first.RawHeight;
        var secondStart = horizontal ? second.RawX : second.RawY;
        var secondLength = horizontal ? second.RawWidth : second.RawHeight;
        return Math.Max(0, Math.Max(firstStart, secondStart) - Math.Min(
            SafeEnd(firstStart, firstLength), SafeEnd(secondStart, secondLength)));
    }

    private static double PositiveDifference(double greater, double lesser)
    {
        var difference = greater - lesser;
        if (double.IsFinite(difference)) return Math.Max(0, difference);
        return greater > lesser ? double.MaxValue : 0;
    }

    private static double SafeEnd(double start, double length)
    {
        var end = start + length;
        return double.IsFinite(end) ? end : double.MaxValue;
    }

    private sealed record NormalizedItem(
        int Index,
        string Key,
        double RawX,
        double RawY,
        double RawWidth,
        double RawHeight,
        double Width,
        double Height,
        MonitorVisualDescriptor Descriptor,
        bool Connected);

    private sealed record AlignmentConstraint(
        int FirstIndex,
        int SecondIndex,
        double SecondMinusFirst,
        double Error,
        string Relationship);

    private sealed class DisjointSet(int count)
    {
        private readonly int[] _parents = Enumerable.Range(0, count).ToArray();

        public bool Union(int first, int second)
        {
            var firstRoot = Find(first);
            var secondRoot = Find(second);
            if (firstRoot == secondRoot) return false;
            _parents[secondRoot] = firstRoot;
            return true;
        }

        public int Find(int item)
        {
            while (_parents[item] != item)
            {
                _parents[item] = _parents[_parents[item]];
                item = _parents[item];
            }
            return item;
        }
    }

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;
    private static double FinitePositive(double value, double fallback) => double.IsFinite(value) && value > 0 ? value : fallback;
    private static double ClampFinite(double value, double minimum, double maximum)
        => Math.Clamp(double.IsFinite(value) ? value : minimum, minimum, maximum);
}

public static class SavedMonitorVisualResolver
{
    public static MonitorVisualDescriptor Resolve(
        MonitorWallpaperProfile profile,
        IEnumerable<MonitorVisualPreference> preferences)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(preferences);
        var all = preferences.ToArray();
        var preference = all.FirstOrDefault(candidate =>
            string.Equals(candidate.MonitorId, profile.MonitorId, StringComparison.OrdinalIgnoreCase));
        if (preference is null && !string.IsNullOrWhiteSpace(profile.MonitorDevicePath))
        {
            var matches = all.Where(candidate => !string.IsNullOrWhiteSpace(candidate.MonitorDevicePath) &&
                string.Equals(candidate.MonitorDevicePath, profile.MonitorDevicePath, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1) preference = matches[0];
        }

        var width = preference?.LastKnownWidth > 0 ? preference.LastKnownWidth : profile.DisplayWidth;
        var height = preference?.LastKnownHeight > 0 ? preference.LastKnownHeight : profile.DisplayHeight;
        if (width <= 0 || height <= 0)
        {
            width = (int)MonitorTopologyLayout.FallbackLandscapeWidth;
            height = (int)MonitorTopologyLayout.FallbackLandscapeHeight;
        }
        if (preference is null)
        {
            var orientation = height > width ? DisplayOrientation.Portrait : DisplayOrientation.Landscape;
            return new(DisplayShellStyle.StandardFlat, orientation,
                DisplayStyleClassifier.CalculateAspectRatio(width, height), null,
                PhysicalSizeConfidence.Unavailable, DisplayStyleSource.SafeFallback, null, null);
        }

        var monitor = new MonitorInfo(profile.MonitorId, profile.MonitorDevicePath ?? profile.MonitorId,
            preference.LastKnownModelName ?? "Saved display", profile.DisplayX, profile.DisplayY, width, height, false,
            ReportedOrientation: preference.LastKnownOrientation,
            ModelName: preference.LastKnownModelName,
            PhysicalWidthMillimeters: preference.LastKnownPhysicalWidthMillimeters,
            PhysicalHeightMillimeters: preference.LastKnownPhysicalHeightMillimeters,
            PhysicalSizeSource: preference.LastKnownPhysicalSizeSource,
            IsInternal: preference.LastKnownIsInternal);
        return MonitorVisualResolver.Resolve(monitor, preference);
    }
}
