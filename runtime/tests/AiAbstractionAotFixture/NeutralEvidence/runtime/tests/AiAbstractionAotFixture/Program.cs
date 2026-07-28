namespace AgenticPrReview.Runtime.AiAbstractionFixture;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        try
        {
            var command = CommandLine.Parse(args);
            return command.Name switch
            {
                "first" => await RunFirstAsync(command),
                "resume" => await RunResumeAsync(command),
                "negative" => await NegativeRunner.RunAsync(command),
                _ => throw new FixtureFailure("APR_AI_COMMAND"),
            };
        }
        catch (FixtureFailure failure)
        {
            Console.Error.WriteLine($"APR_AI_FAIL {failure.Code}");
            return 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("APR_AI_FAIL APR_AI_CANCELLED");
            return 2;
        }
        catch
        {
            Console.Error.WriteLine("APR_AI_FAIL APR_AI_UNEXPECTED");
            return 3;
        }
    }

    private static async Task<int> RunFirstAsync(CommandLine command)
    {
        var input = FixtureJson.ReadFirstInput(command.Required("fixture"));
        var harness = CandidateFactory.Create(
            FixturePhase.First,
            command.Optional("scenario") ?? "happy");
        var result = await FixtureRunner.RunFirstAsync(
            input,
            harness,
            command.Optional("scenario") ?? "happy",
            command.Many("canary"),
            CancellationToken.None);
        OutputCommitter.CommitFirst(
            result.State,
            result.Evidence,
            command.Required("state"),
            command.Required("evidence"));
        WriteProcessMarker(command.Required("process-id"));
        Console.WriteLine($"APR_AI_OK first {harness.CandidateName}");
        return 0;
    }

    private static async Task<int> RunResumeAsync(CommandLine command)
    {
        var input = FixtureJson.ReadResumeInput(command.Required("fixture"));
        var state = FixtureJson.ReadState(command.Required("state"));
        var firstEvidence = FixtureJson.ReadFirstEvidence(
            command.Required("first-evidence"));
        var harness = CandidateFactory.Create(
            FixturePhase.Resume,
            command.Optional("scenario") ?? "happy");
        var result = await FixtureRunner.RunResumeAsync(
            input,
            state,
            harness,
            command.Many("canary"),
            CancellationToken.None);
        OutputCommitter.CommitResume(
            result.Evidence,
            new CombinedEvidence(firstEvidence, result.Evidence),
            command.Required("evidence"),
            command.Required("combined"));
        WriteProcessMarker(command.Required("process-id"));
        Console.WriteLine($"APR_AI_OK resume {harness.CandidateName}");
        return 0;
    }

    private static void WriteProcessMarker(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, Environment.ProcessId.ToString());
    }
}

internal sealed class CommandLine
{
    private readonly Dictionary<string, List<string>> _options;

    private CommandLine(
        string name,
        Dictionary<string, List<string>> options)
    {
        Name = name;
        _options = options;
    }

    internal string Name { get; }

    internal static CommandLine Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new FixtureFailure("APR_AI_COMMAND");
        }
        var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) ||
                index + 1 >= args.Length)
            {
                throw new FixtureFailure("APR_AI_COMMAND");
            }
            var name = args[index][2..];
            if (!options.TryGetValue(name, out var values))
            {
                values = [];
                options.Add(name, values);
            }
            values.Add(args[index + 1]);
        }
        return new CommandLine(args[0], options);
    }

    internal string Required(string name) =>
        _options.TryGetValue(name, out var values) && values.Count == 1
            ? values[0]
            : throw new FixtureFailure("APR_AI_COMMAND");

    internal string? Optional(string name) =>
        !_options.TryGetValue(name, out var values)
            ? null
            : values.Count == 1
                ? values[0]
                : throw new FixtureFailure("APR_AI_COMMAND");

    internal string[] Many(string name) =>
        _options.TryGetValue(name, out var values)
            ? values.ToArray()
            : [];
}
