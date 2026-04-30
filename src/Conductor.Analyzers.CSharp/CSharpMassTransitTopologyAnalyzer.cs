using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Conductor.Core;

namespace Conductor.Analyzers.CSharp;

public sealed class CSharpMassTransitTopologyAnalyzer : ITopologyAnalyzer
{
    private const string EnvToken = "{{env}}";
    private const string EnvPlaceholder = "${env}";

    public TransportTopology Analyze(AnalysisInput input)
    {
        var files = Directory.EnumerateFiles(input.RepoPath, "*.cs", SearchOption.AllDirectories)
            .Where(x => !x.Contains("/bin/") && !x.Contains("/obj/"))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        var syntaxTrees = files.Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path)).ToArray();
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location)
        };

        var compilation = CSharpCompilation.Create(
            "Conductor.Analysis",
            syntaxTrees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var constantLookup = ConstantLookup.Create(compilation.SyntaxTrees);

        var services = new List<ServiceTopology>();
        var allQueues = new HashSet<string>(StringComparer.Ordinal);
        var allTopics = new HashSet<string>(StringComparer.Ordinal);
        var diagnostics = new List<AnalysisDiagnostic>();

        foreach (var tree in syntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();
            var usingNamespaces = root.DescendantNodes()
                .OfType<UsingDirectiveSyntax>()
                .Where(u => u.Name is not null)
                .Select(u => u.Name!.ToString())
                .ToArray();

            var locals = BuildLocalConstantMap(root, semanticModel, constantLookup, usingNamespaces);
            var service = AnalyzeFile(tree.FilePath, root, semanticModel, locals, constantLookup, usingNamespaces, input.Strict, diagnostics);
            if (service is null)
            {
                continue;
            }

            services.Add(service);
            foreach (var endpoint in service.Endpoints)
            {
                allQueues.Add(endpoint.QueueName);
                foreach (var topic in endpoint.SubscribedTopics)
                {
                    allTopics.Add(topic);
                }
            }

            foreach (var messageBinding in service.MessageBindings)
            {
                if (!string.IsNullOrWhiteSpace(messageBinding.TopicName))
                {
                    allTopics.Add(messageBinding.TopicName!);
                }
            }

            foreach (var convention in service.EndpointConventions)
            {
                allQueues.Add(convention.QueueName);
            }
        }

        return new TransportTopology(
            services,
            allQueues.OrderBy(x => x, StringComparer.Ordinal).Select(x => new QueueResource(x)).ToArray(),
            allTopics.OrderBy(x => x, StringComparer.Ordinal).Select(x => new TopicResource(x)).ToArray(),
            diagnostics);
    }

    private static ServiceTopology? AnalyzeFile(
        string sourceFile,
        SyntaxNode root,
        SemanticModel semanticModel,
        IReadOnlyDictionary<string, string> locals,
        ConstantLookup constantLookup,
        IReadOnlyCollection<string> usingNamespaces,
        bool strict,
        ICollection<AnalysisDiagnostic> diagnostics)
    {
        var usingAmazonSqs = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Any(i => i.Expression.ToString().Contains("UsingAmazonSqs", StringComparison.Ordinal));

        if (!usingAmazonSqs)
        {
            return null;
        }

        var endpoints = new Dictionary<string, EndpointBuilder>(StringComparer.Ordinal);
        var messageBindings = new List<MessageBinding>();
        var conventions = new List<EndpointConventionBinding>();
        var receiveFaultPublishExcluded = false;

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var exprText = invocation.Expression.ToString();

            if (exprText.EndsWith("ReceiveEndpoint", StringComparison.Ordinal))
            {
                ParseReceiveEndpoint(invocation, sourceFile, semanticModel, locals, constantLookup, usingNamespaces, strict, diagnostics, endpoints);
            }
            else if (exprText.Contains(".Subscribe", StringComparison.Ordinal))
            {
                ParseSubscribe(invocation, sourceFile, semanticModel, locals, constantLookup, usingNamespaces, strict, diagnostics, endpoints);
            }
            else if (exprText.Contains("SetEntityName", StringComparison.Ordinal))
            {
                ParseSetEntityName(invocation, sourceFile, semanticModel, locals, constantLookup, usingNamespaces, strict, diagnostics, messageBindings);
            }
            else if (exprText.Contains("EndpointConvention.Map", StringComparison.Ordinal))
            {
                ParseEndpointConvention(invocation, sourceFile, semanticModel, locals, constantLookup, usingNamespaces, strict, diagnostics, conventions);
            }
            else if (exprText.Contains("Publish<ReceiveFault>", StringComparison.Ordinal))
            {
                if (invocation.ArgumentList.Arguments.Count > 0 &&
                    invocation.ArgumentList.Arguments[0].Expression is SimpleLambdaExpressionSyntax lambda &&
                    lambda.Body.ToString().Contains("Exclude = true", StringComparison.Ordinal))
                {
                    receiveFaultPublishExcluded = true;
                }
            }
        }

        var endpointTopologies = endpoints.Values
            .OrderBy(x => x.QueueName, StringComparer.Ordinal)
            .Select(x => x.Build())
            .ToArray();

        return new ServiceTopology(
            sourceFile,
            receiveFaultPublishExcluded,
            endpointTopologies,
            messageBindings.OrderBy(x => x.MessageType, StringComparer.Ordinal).ToArray(),
            conventions.OrderBy(x => x.MessageType, StringComparer.Ordinal).ToArray());
    }

    private static void ParseReceiveEndpoint(
        InvocationExpressionSyntax invocation,
        string sourceFile,
        SemanticModel semanticModel,
        IReadOnlyDictionary<string, string> locals,
        ConstantLookup constantLookup,
        IReadOnlyCollection<string> usingNamespaces,
        bool strict,
        ICollection<AnalysisDiagnostic> diagnostics,
        IDictionary<string, EndpointBuilder> endpoints)
    {
        var args = invocation.ArgumentList.Arguments;
        if (args.Count == 0)
        {
            return;
        }

        var queue = ResolveName(args[0].Expression, semanticModel, locals, constantLookup, usingNamespaces);
        if (queue is null)
        {
            if (strict)
            {
                diagnostics.Add(new AnalysisDiagnostic(sourceFile, $"Could not resolve queue name for {invocation} ."));
            }
            return;
        }

        if (!endpoints.TryGetValue(queue, out var builder))
        {
            builder = new EndpointBuilder(queue);
            endpoints.Add(queue, builder);
        }

        if (args.Count > 1 && args[1].Expression is SimpleLambdaExpressionSyntax simpleLambda)
        {
            ParseEndpointLambda(simpleLambda.Body, semanticModel, locals, constantLookup, usingNamespaces, strict, sourceFile, diagnostics, builder);
        }

        if (args.Count > 1 && args[1].Expression is ParenthesizedLambdaExpressionSyntax parenLambda)
        {
            ParseEndpointLambda(parenLambda.Body, semanticModel, locals, constantLookup, usingNamespaces, strict, sourceFile, diagnostics, builder);
        }
    }

    private static void ParseEndpointLambda(
        CSharpSyntaxNode body,
        SemanticModel semanticModel,
        IReadOnlyDictionary<string, string> locals,
        ConstantLookup constantLookup,
        IReadOnlyCollection<string> usingNamespaces,
        bool strict,
        string sourceFile,
        ICollection<AnalysisDiagnostic> diagnostics,
        EndpointBuilder builder)
    {
        IEnumerable<InvocationExpressionSyntax> invocations = body switch
        {
            BlockSyntax block => block.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            InvocationExpressionSyntax single => new[] { single },
            _ => Array.Empty<InvocationExpressionSyntax>()
        };

        foreach (var call in invocations)
        {
            var callText = call.Expression.ToString();

            if (callText.Contains(".DiscardFaultedMessages", StringComparison.Ordinal))
            {
                builder.DiscardFaultedMessages = true;
                continue;
            }

            if (callText.Contains(".DiscardSkippedMessages", StringComparison.Ordinal))
            {
                builder.DiscardSkippedMessages = true;
                continue;
            }

            if (!callText.Contains(".Subscribe", StringComparison.Ordinal))
            {
                continue;
            }

            var args = call.ArgumentList.Arguments;
            if (args.Count == 0)
            {
                continue;
            }

            var topic = ResolveName(args[0].Expression, semanticModel, locals, constantLookup, usingNamespaces);
            if (topic is null)
            {
                if (strict)
                {
                    diagnostics.Add(new AnalysisDiagnostic(sourceFile, $"Could not resolve topic name for {call} ."));
                }
                continue;
            }

            builder.SubscribedTopics.Add(topic);
        }

        IEnumerable<AssignmentExpressionSyntax> assignments = body switch
        {
            BlockSyntax block => block.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            _ => Array.Empty<AssignmentExpressionSyntax>()
        };

        foreach (var assignment in assignments)
        {
            if (assignment.Left.ToString().EndsWith(".PublishFaults", StringComparison.Ordinal) &&
                assignment.Right.IsKind(SyntaxKind.FalseLiteralExpression))
            {
                builder.PublishFaultsDisabled = true;
            }
        }
    }

    private static void ParseSubscribe(
        InvocationExpressionSyntax invocation,
        string sourceFile,
        SemanticModel semanticModel,
        IReadOnlyDictionary<string, string> locals,
        ConstantLookup constantLookup,
        IReadOnlyCollection<string> usingNamespaces,
        bool strict,
        ICollection<AnalysisDiagnostic> diagnostics,
        IDictionary<string, EndpointBuilder> endpoints)
    {
        if (invocation.FirstAncestorOrSelf<LambdaExpressionSyntax>() is null)
        {
            return;
        }

        var receiveEndpoint = invocation.Ancestors().OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(x => x.Expression.ToString().EndsWith("ReceiveEndpoint", StringComparison.Ordinal));

        if (receiveEndpoint is null)
        {
            return;
        }

        var receiveArgs = receiveEndpoint.ArgumentList.Arguments;
        if (receiveArgs.Count == 0)
        {
            return;
        }

        var queue = ResolveName(receiveArgs[0].Expression, semanticModel, locals, constantLookup, usingNamespaces);
        if (queue is null)
        {
            if (strict)
            {
                diagnostics.Add(new AnalysisDiagnostic(sourceFile, $"Could not resolve queue for subscribe call {invocation} ."));
            }
            return;
        }

        if (!endpoints.TryGetValue(queue, out var builder))
        {
            builder = new EndpointBuilder(queue);
            endpoints.Add(queue, builder);
        }

        var args = invocation.ArgumentList.Arguments;
        if (args.Count == 0)
        {
            return;
        }

        var topic = ResolveName(args[0].Expression, semanticModel, locals, constantLookup, usingNamespaces);
        if (topic is null)
        {
            if (strict)
            {
                diagnostics.Add(new AnalysisDiagnostic(sourceFile, $"Could not resolve topic for subscribe call {invocation} ."));
            }
            return;
        }

        builder.SubscribedTopics.Add(topic);
    }

    private static void ParseSetEntityName(
        InvocationExpressionSyntax invocation,
        string sourceFile,
        SemanticModel semanticModel,
        IReadOnlyDictionary<string, string> locals,
        ConstantLookup constantLookup,
        IReadOnlyCollection<string> usingNamespaces,
        bool strict,
        ICollection<AnalysisDiagnostic> diagnostics,
        ICollection<MessageBinding> bindings)
    {
        var args = invocation.ArgumentList.Arguments;
        if (args.Count == 0)
        {
            return;
        }

        var entityName = ResolveName(args[0].Expression, semanticModel, locals, constantLookup, usingNamespaces);
        if (entityName is null)
        {
            if (strict)
            {
                diagnostics.Add(new AnalysisDiagnostic(sourceFile, $"Could not resolve topic for SetEntityName call {invocation} ."));
            }
            return;
        }

        var messageType = "unknown-message";
        var messageCall = invocation.Ancestors().OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(x => x.Expression.ToString().Contains("bus.Message<", StringComparison.Ordinal));

        if (messageCall?.Expression is MemberAccessExpressionSyntax maes && maes.Name is GenericNameSyntax generic)
        {
            messageType = generic.TypeArgumentList.Arguments.ToString();
        }

        bindings.Add(new MessageBinding(messageType, entityName));
    }

    private static void ParseEndpointConvention(
        InvocationExpressionSyntax invocation,
        string sourceFile,
        SemanticModel semanticModel,
        IReadOnlyDictionary<string, string> locals,
        ConstantLookup constantLookup,
        IReadOnlyCollection<string> usingNamespaces,
        bool strict,
        ICollection<AnalysisDiagnostic> diagnostics,
        ICollection<EndpointConventionBinding> conventions)
    {
        var messageType = "unknown-message";
        if (invocation.Expression is MemberAccessExpressionSyntax maes && maes.Name is GenericNameSyntax generic)
        {
            messageType = generic.TypeArgumentList.Arguments.ToString();
        }

        var args = invocation.ArgumentList.Arguments;
        if (args.Count == 0)
        {
            return;
        }

        var queue = TryExtractQueueFromUriExpression(args[0].Expression, semanticModel, locals, constantLookup, usingNamespaces);
        if (queue is null)
        {
            if (strict)
            {
                diagnostics.Add(new AnalysisDiagnostic(sourceFile, $"Could not resolve queue from EndpointConvention.Map call {invocation} ."));
            }
            return;
        }

        conventions.Add(new EndpointConventionBinding(messageType, queue));
    }

    private static string? TryExtractQueueFromUriExpression(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        IReadOnlyDictionary<string, string> locals,
        ConstantLookup constantLookup,
        IReadOnlyCollection<string> usingNamespaces)
    {
        if (expression is not ObjectCreationExpressionSyntax objectCreation ||
            objectCreation.ArgumentList?.Arguments.Count is null or 0)
        {
            return null;
        }

        var uriArg = objectCreation.ArgumentList!.Arguments[0].Expression;
        if (uriArg is not InterpolatedStringExpressionSyntax interpolated)
        {
            return null;
        }

        var lastInterpolation = interpolated.Contents
            .OfType<InterpolationSyntax>()
            .LastOrDefault();

        if (lastInterpolation is null)
        {
            return null;
        }

        return ResolveName(lastInterpolation.Expression, semanticModel, locals, constantLookup, usingNamespaces);
    }

    private static IReadOnlyDictionary<string, string> BuildLocalConstantMap(
        SyntaxNode root,
        SemanticModel semanticModel,
        ConstantLookup constantLookup,
        IReadOnlyCollection<string> usingNamespaces)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            foreach (var variable in field.Declaration.Variables)
            {
                if (variable.Initializer is null)
                {
                    continue;
                }

                var value = ResolveName(variable.Initializer.Value, semanticModel, map, constantLookup, usingNamespaces);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    map[variable.Identifier.Text] = value;
                }
            }
        }

        foreach (var local in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (local.Initializer is null)
            {
                continue;
            }

            var value = ResolveName(local.Initializer.Value, semanticModel, map, constantLookup, usingNamespaces);
            if (!string.IsNullOrWhiteSpace(value))
            {
                map[local.Identifier.Text] = value;
            }
        }

        return map.ToImmutableDictionary(StringComparer.Ordinal);
    }

    private static string? ResolveName(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        IReadOnlyDictionary<string, string> locals,
        ConstantLookup constantLookup,
        IReadOnlyCollection<string> usingNamespaces)
    {
        if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return literal.Token.ValueText;
        }

        if (expression is IdentifierNameSyntax identifier)
        {
            if (locals.TryGetValue(identifier.Identifier.Text, out var localValue))
            {
                return localValue;
            }

            var symbol = semanticModel.GetSymbolInfo(identifier).Symbol;
            return ResolveConstantFromSymbol(symbol, locals);
        }

        if (expression is MemberAccessExpressionSyntax memberAccess)
        {
            var symbol = semanticModel.GetSymbolInfo(memberAccess).Symbol;
            var bySymbol = ResolveConstantFromSymbol(symbol, locals);
            if (bySymbol is not null)
            {
                return bySymbol;
            }

            return constantLookup.TryResolve(memberAccess, usingNamespaces);
        }

        if (expression is InvocationExpressionSyntax invocation)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax ma && ma.Name.Identifier.Text == "Replace")
            {
                var original = ResolveName(ma.Expression, semanticModel, locals, constantLookup, usingNamespaces);
                if (original is null)
                {
                    return null;
                }

                var args = invocation.ArgumentList.Arguments;
                if (args.Count != 2)
                {
                    return original;
                }

                var oldValue = ResolveName(args[0].Expression, semanticModel, locals, constantLookup, usingNamespaces);
                var newValue = ResolveName(args[1].Expression, semanticModel, locals, constantLookup, usingNamespaces);

                if (oldValue is null)
                {
                    return original;
                }

                if (newValue is null)
                {
                    if (string.Equals(oldValue, EnvToken, StringComparison.Ordinal))
                    {
                        return original.Replace(oldValue, EnvPlaceholder, StringComparison.Ordinal);
                    }

                    return original;
                }

                return original.Replace(oldValue, newValue, StringComparison.Ordinal);
            }
        }

        if (expression is InterpolatedStringExpressionSyntax interpolated)
        {
            return string.Concat(interpolated.Contents.Select(c => c.ToString()));
        }

        return null;
    }

    private static string? ResolveConstantFromSymbol(ISymbol? symbol, IReadOnlyDictionary<string, string> locals)
    {
        switch (symbol)
        {
            case IFieldSymbol fieldSymbol when fieldSymbol.HasConstantValue && fieldSymbol.ConstantValue is string s:
                return s;
            case ILocalSymbol localSymbol when localSymbol.HasConstantValue && localSymbol.ConstantValue is string localString:
                return localString;
            case IFieldSymbol fieldSymbol:
                return ResolveFromDeclaringSyntax(fieldSymbol.DeclaringSyntaxReferences, locals);
            case ILocalSymbol localSymbol:
                return ResolveFromDeclaringSyntax(localSymbol.DeclaringSyntaxReferences, locals);
            default:
                return null;
        }
    }

    private static string? ResolveFromDeclaringSyntax(IEnumerable<SyntaxReference> references, IReadOnlyDictionary<string, string> locals)
    {
        foreach (var syntaxReference in references)
        {
            var syntax = syntaxReference.GetSyntax();
            if (syntax is VariableDeclaratorSyntax variable && variable.Initializer is not null)
            {
                if (variable.Initializer.Value is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    return literal.Token.ValueText;
                }

                if (variable.Initializer.Value is IdentifierNameSyntax id && locals.TryGetValue(id.Identifier.Text, out var value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private sealed class EndpointBuilder(string queueName)
    {
        public string QueueName { get; } = queueName;
        public HashSet<string> SubscribedTopics { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Consumers { get; } = new(StringComparer.Ordinal);
        public bool PublishFaultsDisabled { get; set; }
        public bool DiscardFaultedMessages { get; set; }
        public bool DiscardSkippedMessages { get; set; }

        public EndpointTopology Build() => new(
            QueueName,
            SubscribedTopics.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            Consumers.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            PublishFaultsDisabled,
            DiscardFaultedMessages,
            DiscardSkippedMessages);
    }

    private sealed class ConstantLookup(IReadOnlyCollection<ConstantEntry> entries)
    {
        public static ConstantLookup Create(IEnumerable<SyntaxTree> trees)
        {
            var entries = new List<ConstantEntry>();
            foreach (var tree in trees)
            {
                var root = tree.GetRoot();
                foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
                {
                    if (!field.Modifiers.Any(SyntaxKind.ConstKeyword))
                    {
                        continue;
                    }

                    var classDecl = field.FirstAncestorOrSelf<ClassDeclarationSyntax>();
                    if (classDecl is null)
                    {
                        continue;
                    }

                    var ns = GetNamespace(classDecl);
                    foreach (var variable in field.Declaration.Variables)
                    {
                        if (variable.Initializer?.Value is LiteralExpressionSyntax literal &&
                            literal.IsKind(SyntaxKind.StringLiteralExpression))
                        {
                            entries.Add(new ConstantEntry(ns, classDecl.Identifier.Text, variable.Identifier.Text, literal.Token.ValueText));
                        }
                    }
                }
            }

            return new ConstantLookup(entries);
        }

        public string? TryResolve(MemberAccessExpressionSyntax memberAccess, IReadOnlyCollection<string> usingNamespaces)
        {
            if (memberAccess.Expression is not IdentifierNameSyntax id ||
                memberAccess.Name is not IdentifierNameSyntax name)
            {
                return null;
            }

            var candidates = entries
                .Where(e => string.Equals(e.TypeName, id.Identifier.Text, StringComparison.Ordinal) &&
                            string.Equals(e.FieldName, name.Identifier.Text, StringComparison.Ordinal))
                .ToArray();

            if (candidates.Length == 0)
            {
                return null;
            }

            var namespaceMatched = candidates
                .Where(c => usingNamespaces.Contains(c.Namespace, StringComparer.Ordinal))
                .ToArray();

            if (namespaceMatched.Length == 1)
            {
                return namespaceMatched[0].Value;
            }

            if (candidates.Length == 1)
            {
                return candidates[0].Value;
            }

            return namespaceMatched.FirstOrDefault()?.Value ?? candidates.FirstOrDefault()?.Value;
        }

        private static string GetNamespace(SyntaxNode node)
        {
            var fileScoped = node.Ancestors().OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();
            if (fileScoped is not null)
            {
                return fileScoped.Name.ToString();
            }

            var block = node.Ancestors().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
            return block?.Name.ToString() ?? string.Empty;
        }
    }

    private sealed record ConstantEntry(string Namespace, string TypeName, string FieldName, string Value);
}
