using System.Xml.Linq;
using ObjectAnalyzer.Analysis.Pipeline;
using ObjectAnalyzer.Data;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Tests;

public sealed class ArchitectureBoundaryTests
{
    [Fact]
    public void ProjectReferences_StayWithinTheDeclaredLayers()
    {
        var dependencies = new Dictionary<string, string[]>
        {
            ["Core"] = [],
            ["Application"] = ["Core"],
            ["Parsing"] = ["Application", "Core"],
            ["Exporting"] = ["Core"],
            ["Platform"] = ["Core"],
            ["Infrastructure"] = ["Core", "Platform"],
            ["Hosting"] = ["Application", "Core", "Exporting", "Infrastructure", "Parsing", "Platform"]
        };
        foreach (var (module, expected) in dependencies)
        {
            var actual = Project(module).Descendants("ProjectReference")
                .Select(element => Path.GetFileNameWithoutExtension(element.Attribute("Include")!.Value.Replace('\\', '/')))
                .Select(name => name["ObjectAnalyzer.".Length..]).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData("Core")]
    [InlineData("Application")]
    [InlineData("Platform")]
    public void DomainProjects_DoNotBringPackagesOrFrameworks(string module)
    {
        var project = Project(module);
        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Empty(project.Descendants("FrameworkReference"));
        Assert.Empty(project.Descendants("Compile")); // no linked adapter sources hiding in domain assemblies
    }

    [Fact]
    public void RepresentativeTypes_BelongToTheirOwningAssemblies()
    {
        var owners = new Dictionary<Type, string>
        {
            [typeof(AnalysisAccumulator)] = "Core",
            [typeof(SourceRecord)] = "Core",
            [typeof(SchemaInference)] = "Core",
            [typeof(ResultProjection)] = "Core",
            [typeof(QualityMetricsCalculator)] = "Core",
            [typeof(AnalysisExecutor)] = "Application",
            [typeof(IAnalysisInputServices)] = "Application",
            [typeof(DataSources)] = "Parsing",
            [typeof(FieldTransformProcessor)] = "Parsing",
            [typeof(ReportExporter)] = "Exporting",
            [typeof(AnalysisJobService)] = "Platform",
            [typeof(JobResultProjection)] = "Platform",
            [typeof(SqliteAnalysisHistoryStore)] = "Infrastructure",
            [typeof(AuditLogger)] = "Infrastructure",
            [typeof(AnalysisCommandRunner)] = "Hosting",
            [typeof(PlatformServiceCollectionExtensions)] = "Hosting"
        };
        foreach (var (type, owner) in owners)
            Assert.Equal("ObjectAnalyzer." + owner, type.Assembly.GetName().Name);
        Assert.All(typeof(AnalysisAccumulator).Assembly.GetReferencedAssemblies(), reference =>
            Assert.True(reference.Name == "System" || reference.Name!.StartsWith("System.", StringComparison.Ordinal), reference.FullName));
    }

    [Fact]
    public void Application_ExecutesWithCustomPortsAndAnInMemorySource()
    {
        var ports = new MemoryPorts();
        var result = new AnalysisExecutor(ports).Execute(new AnalysisOptions(),
            runContext: new AnalysisRunContext { Source = new MemorySource() });
        Assert.Equal(2, result.Result.TotalObjects);
        Assert.Equal(15, result.Result.FieldStatistics.Single().AvgNumber);
        Assert.Equal(1, ports.TransformersCreated);
        Assert.Equal(1, ports.FailuresApplied);
    }

    [Fact]
    public void AdapterSpecificValidation_HappensBeforeOpeningInput()
    {
        var ports = new MemoryPorts();
        var options = new AnalysisOptions();
        options.Input.RootPath = "unsupported-projection";
        Assert.Throws<NotSupportedException>(() => new AnalysisExecutor(ports).Execute(options));
        Assert.Equal(0, ports.TransformersCreated);
        Assert.False(ports.Opened);
    }

    private static XDocument Project(string module)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ObjectAnalyzer.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return XDocument.Load(Path.Combine(directory.FullName, "src", "ObjectAnalyzer." + module, "ObjectAnalyzer." + module + ".csproj"));
    }

    private sealed class MemorySource : IAnalysisDataSource
    {
        public SourceDescriptor Descriptor { get; } = new("memory", "records", new(true, true, false, false, false, false, SourcePositionKind.Record));
        public IEnumerable<SourceRecord> Read(SourceReadOptions options, CancellationToken cancellationToken = default)
        {
            foreach (var amount in new[] { 10, 20 })
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new SourceRecord(new Dictionary<FieldPath, RecordValue>
                { [FieldPath.Parse("amount")] = RecordValue.FromObject(amount) }, new("memory"));
            }
        }
    }

    private sealed class MemoryPorts : IAnalysisInputServices, IRecordTransformer
    {
        public bool Opened { get; private set; }
        public int TransformersCreated { get; private set; }
        public int FailuresApplied { get; private set; }
        public IAnalysisDataSource Open(AnalysisOptions options, AnalysisRules? rules, IRecordParserRegistry? parsers)
        { Opened = true; throw new NotSupportedException("No external input adapters are installed."); }
        public void ValidateProjection(string expression) => throw new NotSupportedException("Projection is not installed.");
        public IReadOnlyList<Dictionary<string, object?>> ProjectRecord(Dictionary<string, object?> record, string expression)
            => throw new NotSupportedException("Projection is not installed.");
        public IReadOnlyDictionary<string, object?>? DescribeCompression(string path) => null;
        public IRecordTransformer CreateTransformer(AnalysisRules? rules, IEnumerable<string>? fields, string? filter)
        { TransformersCreated++; return this; }
        public bool HasSelectionOrTransforms => false;
        public IEnumerable<Dictionary<string, object?>> Wrap(IEnumerable<Dictionary<string, object?>> objects) => objects;
        public void ApplyFailures(AnalysisResult result) => FailuresApplied++;
    }
}
