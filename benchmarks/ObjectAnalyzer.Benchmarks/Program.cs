using BenchmarkDotNet.Running;
using ObjectAnalyzer.Benchmarks;

if (args is ["--probe", var cardinality, var parallelism, var output])
    ResourceProbe.Run(int.Parse(cardinality, System.Globalization.CultureInfo.InvariantCulture),
        int.Parse(parallelism, System.Globalization.CultureInfo.InvariantCulture), output);
else
    BenchmarkSwitcher.FromAssembly(typeof(AnalysisBenchmarks).Assembly).Run(args);
