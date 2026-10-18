using System.Text.Json;
using ObjectAnalyzer.Analysis.Options;

namespace ObjectAnalyzer.Tests;

public sealed class StateResumeTests : IDisposable
{
    private readonly List<string> _paths = new();

    private string Temp(string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), $"oa-state-{Guid.NewGuid():N}{extension}");
        _paths.Add(path);
        return path;
    }

    [Fact]
    public void Resume_AppendedData_MatchesFullRunIncludingCrossBatchDuplicates()
    {
        string input = Temp(".jsonl"), checkpoint = Temp(".snapshot"), next = Temp(".snapshot");
        File.WriteAllText(input, "{\"x\":1}\n{\"x\":null}\n{\"x\":1}\n");
        var initial = new AnalysisOptions
        {
            Input = { InputPath = input }, Incremental = { CheckpointPath = checkpoint }, Progress = { ShowProgress = false }
        };
        Assert.Equal(0, AnalysisRunner.Run(initial));
        File.AppendAllText(input, "{\"x\":3}\n{\"x\":\"abc\"}\n{\"x\":3}\n");
        Assert.Equal(0, AnalysisRunner.Run(new AnalysisOptions
        {
            Input = { InputPath = input }, Incremental = { ResumeFrom = checkpoint, CheckpointPath = next }, Progress = { ShowProgress = false }
        }));
        var restored = AnalyzerSnapshot.LoadFrom(next).State.Restore().Complete();
        var expected = Analyze(FileParsers.ParseByFormat(input, "jsonl", new ParseOptions()));
        Assert.Equal(expected.TotalObjects, restored.TotalObjects);
        Assert.Equal(expected.TotalUniqueObjects, restored.TotalUniqueObjects);
        Assert.Equal(expected.TotalDuplicateGroups, restored.TotalDuplicateGroups);
        var left = Assert.Single(expected.FieldStatistics);
        var right = Assert.Single(restored.FieldStatistics);
        Assert.Equal(left.AvgNumber, right.AvgNumber);
        Assert.Equal(left.StdDevNumber, right.StdDevNumber);
        Assert.Equal(left.MedianNumber, right.MedianNumber);
        Assert.Equal(left.AvgLength, right.AvgLength);
        Assert.Equal(left.NullCount, right.NullCount);
        using var document = JsonDocument.Parse(File.ReadAllText(next));
        Assert.True(document.RootElement.TryGetProperty("state", out _));
        Assert.False(document.RootElement.TryGetProperty("result", out _));
    }

    [Fact]
    public void Cursor_CountsPhysicalLinesIncludingBlanksAndFilteredRecords()
    {
        string input = Temp(".jsonl"), checkpoint = Temp(".snapshot"), next = Temp(".snapshot");
        File.WriteAllText(input, "{\"x\":1}\n\n{\"x\":2}\n");
        Assert.Equal(0, AnalysisRunner.Run(new AnalysisOptions
        {
            Input = { InputPath = input }, FieldSelection = { Filter = "x > 1" },
            Incremental = { CheckpointPath = checkpoint }, Progress = { ShowProgress = false }
        }));
        var saved = AnalyzerSnapshot.LoadFrom(checkpoint);
        Assert.Equal(3, saved.SourcePosition);
        Assert.Equal(1, saved.State.TotalObjects);
        File.AppendAllText(input, "{\"x\":2}\n\n{\"x\":3}\n");
        Assert.Equal(0, AnalysisRunner.Run(new AnalysisOptions
        {
            Input = { InputPath = input }, FieldSelection = { Filter = "x > 1" },
            Incremental = { ResumeFrom = checkpoint, CheckpointPath = next }, Progress = { ShowProgress = false }
        }));
        saved = AnalyzerSnapshot.LoadFrom(next);
        Assert.Equal(6, saved.SourcePosition);
        Assert.Equal(3, saved.State.TotalObjects);
        Assert.Equal(2, saved.Result.TotalUniqueObjects);
        Assert.Equal(1, saved.Result.TotalDuplicateGroups);
    }

    [Fact]
    public void Resume_RejectsModifiedPrefixOrChangedExecutionConfiguration()
    {
        string input = Temp(".jsonl"), checkpoint = Temp(".snapshot");
        File.WriteAllText(input, "{\"x\":1}\n");
        Assert.Equal(0, AnalysisRunner.Run(new AnalysisOptions
        {
            Input = { InputPath = input }, Incremental = { CheckpointPath = checkpoint }, Progress = { ShowProgress = false }
        }));
        Assert.Equal(1, AnalysisRunner.Run(new AnalysisOptions
        {
            Input = { InputPath = input }, FieldSelection = { Filter = "x > 1" },
            Incremental = { ResumeFrom = checkpoint }, Progress = { ShowProgress = false }
        }));
        File.WriteAllText(input, "{\"x\":2}\n");
        Assert.Equal(1, AnalysisRunner.Run(new AnalysisOptions
        {
            Input = { InputPath = input }, Incremental = { ResumeFrom = checkpoint }, Progress = { ShowProgress = false }
        }));
    }

    [Fact]
    public void Baseline_RejectsAReportWithoutCalculationState()
    {
        string input = Temp(".jsonl"), report = Temp(".json"), output = Temp(".json");
        File.WriteAllText(input, "{\"x\":1}\n");
        File.WriteAllText(report, JsonSerializer.Serialize(new AnalysisResult { TotalObjects = 100 }));
        Assert.Equal(1, AnalysisRunner.Run(new AnalysisOptions
        {
            Input = { InputPath = input }, Incremental = { BaselinePath = report },
            Report = { OutputPath = output }, Progress = { ShowProgress = false }
        }));
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData("object-analyzer.snapshot/v1")]
    [InlineData("object-analyzer.snapshot/v2")]
    public void LegacyCheckpoint_RequiresReanalysis(string schema)
    {
        string path = Temp(".snapshot");
        File.WriteAllText(path, JsonSerializer.Serialize(new { schema, result = new AnalysisResult() }));
        Assert.Throws<InvalidDataException>(() => AnalyzerSnapshot.LoadFrom(path));
    }

    [Fact]
    public void FailedSave_PreservesExistingCheckpoint()
    {
        string path = Temp(".snapshot");
        new AnalyzerSnapshot().SaveTo(path);
        string original = File.ReadAllText(path);
        Assert.Throws<InvalidDataException>(() => new AnalyzerSnapshot
        {
            State = new AnalysisStateSnapshot { TotalObjects = 1 }
        }.SaveTo(path));
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp"));
    }

    public void Dispose()
    {
        foreach (string path in _paths) if (File.Exists(path)) File.Delete(path);
    }
}
