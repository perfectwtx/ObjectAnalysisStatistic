namespace ObjectAnalyzer.Analysis;

/// <summary>One-pass canonical grouping with bounded keys and examples; occurrence counts are never sample counts.</summary>
internal sealed class FuzzyDuplicateCollector(AnalysisResourceLimits limits)
{
    private sealed class Group { public long Count; public List<string> Examples { get; } = new(); }
    private readonly Dictionary<string, Group> _groups = new(StringComparer.Ordinal);
    private long _total;
    private long _bytes;
    public void Observe(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (value.Length > limits.MaxPatternCharacters) throw new AnalysisResourceLimitException("fuzzy-key-characters", limits.MaxPatternCharacters);
        _total++;
        string key = FuzzyDuplicateDetector.Canonicalize(value);
        if (key.Length == 0) return;
        if (!_groups.TryGetValue(key, out var group))
        {
            if (_groups.Count >= limits.MaxFuzzyKeys) throw new AnalysisResourceLimitException("fuzzy-keys", limits.MaxFuzzyKeys);
            Reserve(key.Length * 2L + 64);
            group = new(); _groups.Add(key, group);
        }
        group.Count++;
        string example = value[..Math.Min(value.Length, limits.MaxExampleCharacters)];
        if (group.Examples.Count < 5 && !group.Examples.Contains(example))
        { Reserve(example.Length * 2L + 24); group.Examples.Add(example); }
    }
    private void Reserve(long bytes)
    {
        if (_bytes + bytes > limits.MaxRetainedPayloadBytes) throw new AnalysisResourceLimitException("fuzzy-payload-bytes", limits.MaxRetainedPayloadBytes);
        _bytes += bytes;
    }
    public FuzzyDuplicateReport Complete(CancellationToken cancellationToken)
    {
        var duplicates = _groups.Where(pair => pair.Value.Count >= 2).ToList();
        int groupCount = duplicates.Count; long duplicateCount = duplicates.Sum(pair => pair.Value.Count);
        // The heuristic search covers singleton canonical keys; bound quadratic work independently of configured retention.
        var singletons = _groups.Where(pair => pair.Value.Count == 1).Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray();
        if (singletons.Length <= 5000)
        {
            var tokens = singletons.Select(FuzzyDuplicateDetector.BuildTokenSet).ToArray();
            var parent = Enumerable.Range(0, tokens.Length).ToArray();
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            for (int i = 0; i < tokens.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int j = i + 1; j < tokens.Length; j++)
                    if (FuzzyDuplicateDetector.Jaccard(tokens[i], tokens[j]) >= FuzzyDuplicateDetector.FuzzySimilarityThreshold)
                        parent[Find(i)] = Find(j);
            }
            foreach (var bucket in Enumerable.Range(0, tokens.Length).GroupBy(Find))
                if (bucket.Count() >= 2) { groupCount++; duplicateCount += bucket.Count(); }
        }
        return new FuzzyDuplicateReport
        {
            TotalSamples = _total, GroupCount = groupCount, SimilarDuplicates = duplicateCount,
            Groups = duplicates.OrderByDescending(pair => pair.Value.Count).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(FuzzyDuplicateDetector.MaxReportedGroups).Select(pair => new FuzzyDuplicateGroup
                {
                    CanonicalKey = pair.Key, Count = pair.Value.Count, AverageSimilarity = 1,
                    RepresentativeJson = pair.Value.Examples.FirstOrDefault() ?? "", Examples = pair.Value.Examples.ToList()
                }).ToList()
        };
    }
}
