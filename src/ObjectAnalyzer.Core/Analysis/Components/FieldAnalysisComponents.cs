using System.Text.Json;

namespace ObjectAnalyzer.Analysis;

internal interface IFieldMetricsProvider { InternalFieldMetrics Metrics { get; } }

internal sealed class FieldAnalysisComponents
{
    private readonly FieldAnalyzerContext _context;
    private readonly Dictionary<string, IFieldAnalyzerState> _states = new(StringComparer.Ordinal);
    private readonly IFieldAnalyzer[] _enabled;

    public FieldAnalysisComponents(FieldAnalyzerRegistry registry, FieldAnalyzerContext context)
    {
        _context = context;
        _enabled = registry.Factories.Where(f => f.IsEnabled(context)).ToArray();
    }

    public void Observe(RecordValue value)
    {
        foreach (var factory in _enabled)
        {
            if (!factory.InputKinds.Contains(value.Kind)) continue;
            if (!_states.TryGetValue(factory.Id, out var state))
            {
                state = factory.CreateState(_context);
                _states.Add(factory.Id, state);
            }
            state.Observe(value);
        }
    }

    public void Merge(FieldAnalysisComponents other)
    {
        foreach (var pair in other._states)
        {
            if (!_states.TryGetValue(pair.Key, out var target))
            {
                target = _enabled.Single(f => f.Id == pair.Key).CreateState(_context);
                _states.Add(pair.Key, target);
            }
            target.Merge(pair.Value);
        }
    }

    public void Complete(FieldStatistic result)
    {
        foreach (var factory in _enabled)
            if (_states.TryGetValue(factory.Id, out var state)) state.Complete(result);
    }

    public InternalFieldMetrics MetricsView()
    {
        var metrics = new InternalFieldMetrics(int.MaxValue);
        foreach (var provider in _states.Values.OfType<IFieldMetricsProvider>()) metrics.MergeFrom(provider.Metrics);
        return metrics;
    }

    public List<FieldAnalyzerStateSnapshot> Capture() => _states.OrderBy(p => p.Key, StringComparer.Ordinal)
        .Select(p => new FieldAnalyzerStateSnapshot { Id = p.Key, Data = p.Value.Capture().Clone() }).ToList();

    public void Restore(IReadOnlyList<FieldAnalyzerStateSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            var factory = _enabled.SingleOrDefault(f => f.Id == snapshot.Id)
                ?? throw new InvalidDataException($"Unexpected analyzer state '{snapshot.Id}'.");
            if (snapshot.Data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                throw new InvalidDataException("Missing analyzer state payload.");
            if (!_states.TryAdd(snapshot.Id, factory.RestoreState(_context, snapshot.Data)))
                throw new InvalidDataException("Duplicate analyzer state.");
        }
    }

    public void ValidateObservedKinds(IEnumerable<string> kinds)
    {
        var observed = new HashSet<RecordValueKind>();
        foreach (string kind in kinds)
        {
            if (!Enum.TryParse(kind, out RecordValueKind parsed) || Enum.GetName(parsed) != kind)
                throw new InvalidDataException("Unknown observed value kind.");
            observed.Add(parsed);
        }
        foreach (var factory in _enabled)
            if (factory.InputKinds.Any(observed.Contains) && !_states.ContainsKey(factory.Id))
                throw new InvalidDataException($"Missing observed analyzer state '{factory.Id}'.");
    }
}
