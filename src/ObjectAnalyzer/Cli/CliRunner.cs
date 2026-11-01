using Spectre.Console;

namespace ObjectAnalyzer.Cli;

internal static class CliRunner
{
    public static int Run(CliArguments cli, AnalysisCommandRunner runner, CancellationToken cancellationToken)
    {
        var input = cli.Input;
        var output = cli.Output;
        var noFlatten = cli.NoFlatten;
        var maxValues = cli.MaxValues;
        var maxTrackedValues = cli.MaxTrackedValues;
        var allowUnknownRules = cli.AllowUnknownRules;
        var noCsvNumberInference = cli.NoCsvNumberInference;
        var fields = cli.Fields;
        var where = cli.Where;
        var maxParallel = cli.MaxParallel;
        var violations = cli.Violations;
        var quality = cli.Quality;
        var failOnViolations = cli.FailOnViolations;
        var sampleReservoir = cli.SampleReservoir;
        var sampleStep = cli.SampleStep;
        var exportSchema = cli.ExportSchema;
        var connection = cli.Connection;
        var query = cli.Query;
        var provider = cli.Provider;
        var piiOff = cli.PiiOff;
        var failOnPiiLevel = cli.FailOnPiiLevel;
        var failOnOutlierRatio = cli.FailOnOutlierRatio;
        var schemaSnapshotPath = cli.SchemaSnapshot;
        var diffSchemaBaseline = cli.DiffSchema;
        var schemaDiffReportPath = cli.SchemaDiffReport;
        var allowBreakingSchemaChanges = cli.AllowBreakingSchemaChanges;
        var rootPath = cli.RootPath;
        var recordPath = cli.RecordPath;
        var auditLogPath = cli.AuditLog;
        var operatorValue = cli.Operator;
        var noProgress = cli.NoProgress;
        var checkpointPath = cli.Checkpoint;
        var resumeFrom = cli.Resume;
        var baselinePath = cli.Baseline;
        var maxMemoryMB = cli.MaxMemoryMB;
        var maxMemoryMode = cli.MaxMemoryMode.Trim().ToLowerInvariant() switch
        {
            "hard" => MaxMemoryMode.Hard,
            _ => MaxMemoryMode.Soft
        };
        var preflightOnly = cli.PreflightOnly;
        var yes = cli.Yes;
        var rules = cli.Rules;
        var piiReport = cli.PiiReport;

        if (cli.GenerateTestExcel)
        {
            TestFileGenerator.GenerateAll();
            return 0;
        }

        if (maxParallel < 1 && maxParallel != -1)
        {
            AnsiConsole.MarkupLine("[red]错误: --parallel 必须是正整数 (>= 1) 或 -1 (默认)。[/]");
            return 1;
        }

        try
        {
            Sampling.Validate(sampleReservoir, sampleStep);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]错误: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        if (!string.IsNullOrEmpty(query) && string.IsNullOrEmpty(connection))
        {
            AnsiConsole.MarkupLine("[red]错误: 使用 --query 时必须同时提供 --connection。[/]");
            return 1;
        }

        if (!string.IsNullOrWhiteSpace(failOnPiiLevel))
        {
            string normalized = failOnPiiLevel.Trim().ToUpperInvariant();
            if (normalized is not ("A" or "B" or "C"))
            {
                AnsiConsole.MarkupLine("[red]错误: --fail-on-pii-level 只接受 A / B / C。[/]");
                return 1;
            }
            failOnPiiLevel = normalized;
        }

        if (piiOff && !string.IsNullOrWhiteSpace(failOnPiiLevel))
        {
            AnsiConsole.MarkupLine("[red]错误: --pii-off 与 --fail-on-pii-level 互斥（关闭 PII 识别后无法评估合规等级）。[/]");
            return 1;
        }

        if (piiOff && piiReport != null)
        {
            AnsiConsole.MarkupLine("[yellow]警告: 已指定 --pii-off，忽略 --pii-report。[/]");
            piiReport = null;
        }

        if (input == null && string.IsNullOrEmpty(connection))
        {
            AnsiConsole.MarkupLine("[red]错误: 未指定输入文件或数据库连接。使用 -i 指定文件，或使用 --connection + --query 直连数据库。[/]");
            AnsiConsole.MarkupLine("[grey]提示: 使用 -h 或 --help 查看使用说明。[/]");
            return 1;
        }

        if (!string.IsNullOrWhiteSpace(rootPath) && (string.Equals(Path.GetExtension(input?.FullName ?? ""), ".jsonl", StringComparison.OrdinalIgnoreCase)
            || input?.FullName.EndsWith(".jsonl.gz", StringComparison.OrdinalIgnoreCase) == true))
        {
            AnsiConsole.MarkupLine("[red]错误: --root-path 仅对整体 JSON 文件生效；JSONL 是流式逐行结构，请在每行使用 --record-path。[/]");
            return 1;
        }

        // P6.2 早期校验：--resume / --baseline / --checkpoint 互斥关系与输入文件存在性
        if (!string.IsNullOrWhiteSpace(resumeFrom) && input == null)
        {
            AnsiConsole.MarkupLine("[red]错误: --resume 必须与 -i 输入文件同时指定（用于校验源文件一致性）。[/]");
            return 1;
        }
        if (!string.IsNullOrWhiteSpace(checkpointPath) && !string.IsNullOrWhiteSpace(resumeFrom) &&
            string.Equals(Path.GetFullPath(checkpointPath!), Path.GetFullPath(resumeFrom!), StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[red]错误: --checkpoint 与 --resume 指向同一文件；为避免相互覆盖，请使用不同路径。[/]");
            return 1;
        }
        if (!string.IsNullOrWhiteSpace(baselinePath) && !string.IsNullOrWhiteSpace(resumeFrom) &&
            string.Equals(Path.GetFullPath(baselinePath!), Path.GetFullPath(resumeFrom!), StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[red]错误: --baseline 与 --resume 指向同一文件；语义冲突，请使用不同路径。[/]");
            return 1;
        }

        var (schemaFormat, schemaOutput) = SplitColonSuffix(exportSchema);

        // ── 预检（与 Web 端的 PreflightChecker 共享 Core 同一份实现）──
        // 触发条件：有规则文件 + 文件型输入（数据库场景不参与预检）。
        // 行为：
        //   - --preflight-only：跑预检后退出，不进行正式分析
        //   - 警告时：交互式提示继续/取消（--yes 跳过；CI 模式自动通过）
        //   - 无警告：直接继续
        if (rules != null && input != null && !string.IsNullOrEmpty(connection))
        {
            AnsiConsole.MarkupLine("[yellow]警告: --connection 模式下预检不可用，跳过预检。[/]");
        }
        else if (rules != null && input != null)
        {
            var preflightOutcome = CliPreflight.Run(input, rules, noFlatten, allowUnknownRules, noCsvNumberInference);
            if (preflightOutcome == PreflightOutcome.AbortWithError)
            {
                return 1;
            }
            if (preflightOutcome == PreflightOutcome.WarningFound)
            {
                if (preflightOnly)
                {
                    AnsiConsole.MarkupLine("[red]预检失败（--preflight-only 模式已退出，不进行正式分析）。[/]");
                    return 2;
                }
                if (yes)
                {
                    AnsiConsole.MarkupLine("[yellow]--yes 已设置，继续分析（不交互确认）。[/]");
                }
                else
                {
                    bool proceed = AnsiConsole.Confirm("[yellow]规则与数据字段不匹配。是否继续分析？[/]", defaultValue: false);
                    if (!proceed)
                    {
                        AnsiConsole.MarkupLine("[grey]已取消。[/]");
                        return 130; // 标准 SIGINT 退出码
                    }
                }
            }
        }

        var options = new AnalysisOptions
        {
            Input =
            {
                InputPath = input?.FullName ?? "",
                ConnectionString = connection,
                Query = query,
                DbProvider = provider,
                RootPath = rootPath,
                RecordPath = recordPath
            },
            Parse =
            {
                Flatten = !noFlatten,
                CsvInferNumbers = !noCsvNumberInference,
                MaxValuesToShow = maxValues,
                MaxTrackedValues = maxTrackedValues,
                MaxDegreeOfParallelism = maxParallel
            },
            FieldSelection =
            {
                Fields = fields,
                Filter = where
            },
            Report =
            {
                OutputPath = output?.FullName,
                ViolationsPath = violations?.FullName,
                QualityPath = quality?.FullName,
                ExportSchema = schemaFormat,
                SchemaOutputPath = schemaOutput
            },
            Rules =
            {
                RulesFile = rules?.FullName,
                AllowUnknownRules = allowUnknownRules
            },
            Sampling =
            {
                SampleReservoir = sampleReservoir,
                SampleStep = sampleStep
            },
            Pii =
            {
                PiiPath = piiReport?.FullName,
                PiiOff = piiOff
            },
            Gates =
            {
                FailOnViolations = failOnViolations,
                FailOnOutlierRatio = failOnOutlierRatio,
                AllowBreakingSchemaChanges = allowBreakingSchemaChanges,
                FailOnPiiLevel = failOnPiiLevel
            },
            Schema =
            {
                SchemaSnapshotPath = schemaSnapshotPath,
                DiffSchemaBaseline = diffSchemaBaseline,
                SchemaDiffReportPath = schemaDiffReportPath
            },
            Audit =
            {
                AuditLogPath = auditLogPath,
                Operator = operatorValue
            },
            Progress =
            {
                ShowProgress = !noProgress
            },
            Incremental =
            {
                CheckpointPath = checkpointPath,
                ResumeFrom = resumeFrom,
                BaselinePath = baselinePath
            },
            Memory =
            {
                MaxMemoryMB = maxMemoryMB,
                MaxMemoryMode = maxMemoryMode
            }
        };

        return runner.Run(options, cancellationToken);
    }

    /// <summary>
    /// 切分 "前缀[:后缀]"（如 "File.cs:ClassName"、"csharp:out.cs"）。
    /// 跳过 Windows 盘符冒号（如 "C:\x" 中的 'C:'），因此 "csharp:C:\out.cs" 会正确切分为 ("csharp","C:\out.cs")。
    /// 无可用冒号时原样作为路径。
    /// </summary>
    private static (string? path, string? suffix) SplitColonSuffix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (null, null);
        bool driveLetter = value.Length >= 2 && value[1] == ':' && char.IsAsciiLetter(value[0]);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != ':')
                continue;
            if (driveLetter && i == 1)
                continue; // 盘符冒号不算分隔符
            if (i > 0 && i < value.Length - 1)
                return (value[..i], value[(i + 1)..]);
            break;
        }
        return (value, null);
    }
}
