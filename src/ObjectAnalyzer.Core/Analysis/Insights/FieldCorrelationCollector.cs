namespace ObjectAnalyzer.Analysis;

/// <summary>Mergeable priority reservoir of complete rows; columns always describe the same objects.</summary>
public sealed class FieldCorrelationCollector
{
    private readonly int _maxRows;
    private readonly int _maxFields;
    private readonly Random _rng;
    private readonly HashSet<string> _fields = new(StringComparer.Ordinal);
    private readonly PriorityQueue<Dictionary<string, double?>, double> _rows = new();
    public FieldCorrelationCollector(int maxRows = 20_000, int? seed = null, int maxFields = 32)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRows);
        ArgumentOutOfRangeException.ThrowIfNegative(maxFields);
        _maxRows = maxRows; _maxFields = maxFields;
        _rng = seed.HasValue ? new Random(seed.Value) : new Random();
    }
    public int MaxRows => _maxRows;
    public long Seen { get; private set; }
    public int SampleCount => _rows.Count;
    public IReadOnlyDictionary<string, List<double?>> Samples
    {
        get
        {
            var rows = _rows.UnorderedItems.Select(item => item.Element).ToArray();
            return _fields.ToDictionary(name => name,
                name => rows.Select(row => row.GetValueOrDefault(name)).ToList(), StringComparer.Ordinal);
        }
    }
    private void Register(IEnumerable<string> fields)
    {
        foreach (var field in fields)
            if (!_fields.Contains(field))
            {
                if (_fields.Count >= _maxFields) throw new AnalysisResourceLimitException("correlation-fields", _maxFields);
                _fields.Add(field);
            }
    }
    public void RecordObject(IReadOnlyDictionary<string, double?> row)
    {
        Register(row.Keys); Seen++;
        double priority = _rng.NextDouble();
        if (_maxRows == 0 || _rows.Count == _maxRows && _rows.TryPeek(out _, out double min) && min >= priority) return;
        Add(new Dictionary<string, double?>(row, StringComparer.Ordinal), priority);
    }
    private void Add(Dictionary<string, double?> row, double priority)
    {
        if (_maxRows == 0) return;
        if (_rows.Count < _maxRows) _rows.Enqueue(row, priority);
        else if (_rows.TryPeek(out _, out double min) && priority > min) _rows.DequeueEnqueue(row, priority);
    }
    public void Clear() { _rows.Clear(); _fields.Clear(); Seen = 0; }
    public void MergeFrom(FieldCorrelationCollector other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(this, other)) throw new ArgumentException("Cannot merge a reservoir into itself.");
        if (_maxRows != other._maxRows || _maxFields != other._maxFields) throw new ArgumentException("Reservoir limits differ.");
        Register(other._fields);
        foreach (var item in other._rows.UnorderedItems) Add(new(item.Element, StringComparer.Ordinal), item.Priority);
        Seen += other.Seen;
    }
}
