using FoxMouse.Core;

return TraceToolProgram.Run(args);

internal static class TraceToolProgram
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintUsage();
                return args.Length == 0 ? 2 : 0;
            }

            return args[0].ToLowerInvariant() switch
            {
                "generate" => Generate(args),
                "verify" => Verify(args),
                _ => UnknownCommand(args[0]),
            };
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or MotionTraceFormatException
            or ArgumentException)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 2;
        }
    }

    private static int Generate(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("generate requires exactly one output directory.");
            return 2;
        }

        var directory = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(directory);
        foreach (var fixture in BuiltInTraceCorpus.Create())
        {
            var tracePath = Path.Combine(directory, $"{fixture.Name}.fmotion.jsonl");
            var expectationPath = Path.Combine(directory, $"{fixture.Name}.expect.json");
            MotionTraceCodec.Write(tracePath, fixture.Trace);
            TraceExpectationCodec.Write(expectationPath, fixture.Expectation);
            Console.WriteLine($"generated {tracePath}");
            Console.WriteLine($"generated {expectationPath}");
        }

        return 0;
    }

    private static int Verify(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("verify requires one trace file or corpus directory.");
            return 2;
        }

        var input = Path.GetFullPath(args[1]);
        var tracePaths = Directory.Exists(input)
            ? Directory.EnumerateFiles(input, "*.fmotion.jsonl", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [input];

        if (tracePaths.Length == 0)
        {
            Console.Error.WriteLine($"error: no .fmotion.jsonl files found in {input}");
            return 2;
        }

        var failed = 0;
        foreach (var tracePath in tracePaths)
        {
            var expectationPath = GetExpectationPath(tracePath);
            if (!File.Exists(expectationPath))
            {
                Console.Error.WriteLine($"FAIL {tracePath}: missing {expectationPath}");
                failed++;
                continue;
            }

            var trace = MotionTraceCodec.Read(tracePath);
            var expectation = TraceExpectationCodec.Read(expectationPath);
            var result = TraceVerifier.Verify(trace, expectation);
            if (result.Success)
            {
                Console.WriteLine(
                    $"PASS {Path.GetFileName(tracePath)} "
                    + $"triggers={result.TriggerTimestampsMicroseconds.Count} "
                    + $"maxScore={result.MaximumScore:F3}");
                continue;
            }

            failed++;
            Console.Error.WriteLine($"FAIL {tracePath}");
            foreach (var error in result.Errors)
            {
                Console.Error.WriteLine($"  {error}");
            }
        }

        Console.WriteLine($"verified={tracePaths.Length} failed={failed}");
        return failed == 0 ? 0 : 1;
    }

    private static string GetExpectationPath(string tracePath)
    {
        const string suffix = ".fmotion.jsonl";
        return tracePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? string.Concat(tracePath.AsSpan(0, tracePath.Length - suffix.Length), ".expect.json")
            : string.Concat(tracePath, ".expect.json");
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"unknown command: {command}");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("FoxMouse.TraceTool");
        Console.WriteLine("  generate <output-directory>");
        Console.WriteLine("  verify <trace-file-or-directory>");
    }
}
