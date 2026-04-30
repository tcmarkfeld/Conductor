using Conductor.Analyzers.CSharp;
using Conductor.Core;
using Conductor.Emitters.IamJson;

var exitCode = await MainAsync(args);
return exitCode;

static Task<int> MainAsync(string[] args)
{
    var parsed = CliArgs.Parse(args);
    if (!parsed.IsValid)
    {
        Console.Error.WriteLine(parsed.ErrorMessage);
        Console.Error.WriteLine(CliArgs.HelpText);
        return Task.FromResult(2);
    }

    if (parsed.Command != "generate")
    {
        Console.Error.WriteLine($"Unsupported command: {parsed.Command}");
        Console.Error.WriteLine(CliArgs.HelpText);
        return Task.FromResult(2);
    }

    IPolicyEmitter emitter = parsed.Format switch
    {
        "iam-json" => new IamJsonPolicyEmitter(),
        _ => throw new InvalidOperationException($"Unsupported format: {parsed.Format}")
    };

    if (parsed.Scope == "folder")
    {
        return Task.FromResult(GeneratePerFolder(parsed, emitter));
    }

    return Task.FromResult(GenerateSingle(parsed, emitter));
}

static int GenerateSingle(CliArgs parsed, IPolicyEmitter emitter)
{
    var analyzer = new CSharpMassTransitTopologyAnalyzer();
    var topology = analyzer.Analyze(new AnalysisInput(parsed.RepoPath!, parsed.Strict));

    if (!PrintDiagnostics(topology, parsed.Strict))
    {
        return 1;
    }

    var emitOptions = new EmitOptions(parsed.Region, parsed.AccountId, parsed.Env);
    var json = emitter.Emit(topology, emitOptions);

    File.WriteAllText(parsed.OutputPath!, json + Environment.NewLine);
    Console.WriteLine($"Wrote policy to {parsed.OutputPath}");
    Console.WriteLine($"Queues: {topology.Queues.Count}, Topics: {topology.Topics.Count}, Services: {topology.Services.Count}");
    return 0;
}

static int GeneratePerFolder(CliArgs parsed, IPolicyEmitter emitter)
{
    var analyzer = new CSharpMassTransitTopologyAnalyzer();
    var fullTopology = analyzer.Analyze(new AnalysisInput(parsed.RepoPath!, parsed.Strict));
    var folders = Directory.EnumerateDirectories(parsed.RepoPath!)
        .OrderBy(x => x, StringComparer.Ordinal)
        .ToArray();

    Directory.CreateDirectory(parsed.OutputPath!);

    var emitOptions = new EmitOptions(parsed.Region, parsed.AccountId, parsed.Env);
    var hadStrictError = false;
    var generated = 0;

    foreach (var folder in folders)
    {
        var folderName = Path.GetFileName(folder);
        var topology = FilterTopologyForFolder(fullTopology, folder);

        if (!PrintDiagnostics(topology, parsed.Strict, folderName))
        {
            hadStrictError = true;
            continue;
        }

        if (topology.Services.Count == 0 || (topology.Queues.Count == 0 && topology.Topics.Count == 0))
        {
            continue;
        }

        var json = emitter.Emit(topology, emitOptions);
        var outputFile = Path.Combine(parsed.OutputPath!, $"{SanitizeFileName(folderName)}.policy.json");
        File.WriteAllText(outputFile, json + Environment.NewLine);
        Console.WriteLine($"Wrote {outputFile} (Queues: {topology.Queues.Count}, Topics: {topology.Topics.Count}, Services: {topology.Services.Count})");
        generated++;
    }

    Console.WriteLine($"Generated {generated} policy file(s) into {parsed.OutputPath}");
    return hadStrictError ? 1 : 0;
}

static TransportTopology FilterTopologyForFolder(TransportTopology fullTopology, string folderPath)
{
    var normalizedFolder = Path.GetFullPath(folderPath);
    var services = fullTopology.Services
        .Where(s => Path.GetFullPath(s.SourceFile).StartsWith(normalizedFolder + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        .ToArray();

    var diagnostics = fullTopology.Diagnostics
        .Where(d => Path.GetFullPath(d.SourceFile).StartsWith(normalizedFolder + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        .ToArray();

    var queueNames = new HashSet<string>(StringComparer.Ordinal);
    var topicNames = new HashSet<string>(StringComparer.Ordinal);

    foreach (var service in services)
    {
        foreach (var endpoint in service.Endpoints)
        {
            queueNames.Add(endpoint.QueueName);
            foreach (var topic in endpoint.SubscribedTopics)
            {
                topicNames.Add(topic);
            }
        }

        foreach (var convention in service.EndpointConventions)
        {
            queueNames.Add(convention.QueueName);
        }

        foreach (var binding in service.MessageBindings)
        {
            if (!string.IsNullOrWhiteSpace(binding.TopicName))
            {
                topicNames.Add(binding.TopicName!);
            }
        }
    }

    return new TransportTopology(
        services,
        queueNames.OrderBy(x => x, StringComparer.Ordinal).Select(x => new QueueResource(x)).ToArray(),
        topicNames.OrderBy(x => x, StringComparer.Ordinal).Select(x => new TopicResource(x)).ToArray(),
        diagnostics);
}

static bool PrintDiagnostics(TransportTopology topology, bool strict, string? scope = null)
{
    var prefix = string.IsNullOrWhiteSpace(scope) ? string.Empty : $"[{scope}] ";
    if (strict && topology.HasErrors)
    {
        foreach (var diagnostic in topology.Diagnostics)
        {
            Console.Error.WriteLine($"[error] {prefix}{diagnostic.SourceFile}: {diagnostic.Message}");
        }
        return false;
    }

    foreach (var diagnostic in topology.Diagnostics)
    {
        Console.Error.WriteLine($"[warn] {prefix}{diagnostic.SourceFile}: {diagnostic.Message}");
    }

    return true;
}

static string SanitizeFileName(string folderName)
{
    var invalidChars = Path.GetInvalidFileNameChars();
    return string.Concat(folderName.Select(ch => invalidChars.Contains(ch) ? '_' : ch));
}

internal sealed record CliArgs(
    string Command,
    string? RepoPath,
    string? OutputPath,
    string Scope,
    string Format,
    bool Strict,
    string Region,
    string AccountId,
    string Env,
    bool IsValid,
    string? ErrorMessage)
{
    public const string HelpText = """
Usage:
  conductor generate --repo /path --out policy.json [--scope repo|folder] [--format iam-json] [--strict true|false] [--region value] [--account-id value] [--env value]

Notes:
  scope=repo   -> --out is a file path
  scope=folder -> --out is an output directory; one policy per top-level folder
""";

    public static CliArgs Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return Invalid("Missing command.");
        }

        var command = args[0].Trim();
        string? repo = null;
        string? output = null;
        var scope = "repo";
        var format = "iam-json";
        var strict = true;
        var region = "${region}";
        var accountId = "${account_id}";
        var env = "${env}";

        for (var i = 1; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                return Invalid($"Unknown token: {token}");
            }

            if (i + 1 >= args.Length)
            {
                return Invalid($"Missing value for {token}");
            }

            var value = args[++i];
            switch (token)
            {
                case "--repo":
                    repo = value;
                    break;
                case "--out":
                    output = value;
                    break;
                case "--format":
                    format = value;
                    break;
                case "--scope":
                    scope = value;
                    break;
                case "--strict":
                    if (!bool.TryParse(value, out strict))
                    {
                        return Invalid("--strict must be true or false");
                    }
                    break;
                case "--region":
                    region = value;
                    break;
                case "--account-id":
                    accountId = value;
                    break;
                case "--env":
                    env = value;
                    break;
                default:
                    return Invalid($"Unknown option: {token}");
            }
        }

        if (string.IsNullOrWhiteSpace(repo))
        {
            return Invalid("Missing --repo");
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            return Invalid("Missing --out");
        }

        if (!string.Equals(scope, "repo", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(scope, "folder", StringComparison.OrdinalIgnoreCase))
        {
            return Invalid("--scope must be repo or folder");
        }

        return new CliArgs(command, Path.GetFullPath(repo), Path.GetFullPath(output), scope.ToLowerInvariant(), format, strict, region, accountId, env, true, null);
    }

    private static CliArgs Invalid(string message)
        => new("", null, null, "repo", "iam-json", true, "${region}", "${account_id}", "${env}", false, message);
}
