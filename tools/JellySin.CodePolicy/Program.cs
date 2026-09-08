using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using SonarAnalyzer.CSharp.Rules;

namespace JellySin.CodePolicy;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        if (args is ["--self-test"]) return await SelfTestAsync(cancellation.Token);
        if (args.Length > 1) return Fail("Usage: JellySin.CodePolicy [source-directory | --self-test]");
        var directory = Path.GetFullPath(args.FirstOrDefault() ?? "src");
        if (!Directory.Exists(directory)) return Fail("Source directory does not exist.");
        var files = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .Order(StringComparer.Ordinal).Take(2049).ToArray();
        if (files.Length is 0 or > 2048) return Fail("Source file count must be between 1 and 2048.");
        var trees = new List<SyntaxTree>(files.Length);
        foreach (var file in files)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (new FileInfo(file).Length > 2_000_000) return Fail("Source file exceeds 2 MB bound.");
            trees.Add(CSharpSyntaxTree.ParseText(await File.ReadAllTextAsync(file, cancellation.Token), path: file, cancellationToken: cancellation.Token));
        }
        var findings = await InspectAsync(trees, cancellation.Token);
        foreach (var finding in findings) await Console.Error.WriteLineAsync(finding);
        Console.WriteLine($"Code policy: {files.Length} files, {findings.Count} findings; function limits 120 lines / 60 statements, Sonar S3776 threshold 30.");
        return findings.Count == 0 ? 0 : 1;
    }

    public static async Task<List<string>> InspectAsync(IEnumerable<SyntaxTree> trees, CancellationToken cancellationToken)
    {
        var findings = new List<string>();
        foreach (var tree in trees)
        {
            var root = await tree.GetRootAsync(cancellationToken);
            foreach (var diagnostic in tree.GetDiagnostics(cancellationToken).Where(item => item.Severity == DiagnosticSeverity.Error)) findings.Add(diagnostic.ToString());
            foreach (var function in root.DescendantNodes().Where(IsFunction)) InspectFunction(function, findings);
            foreach (var jump in root.DescendantNodes().OfType<GotoStatementSyntax>()) findings.Add(At(jump, "JS003: goto is forbidden."));
            foreach (var loop in root.DescendantNodes().OfType<WhileStatementSyntax>().Where(loop => loop.Condition.IsKind(SyntaxKind.TrueLiteralExpression)))
                findings.Add(At(loop, "JS004: use an explicit bound or cancellation condition instead of while(true)."));
            foreach (var loop in root.DescendantNodes().OfType<ForStatementSyntax>().Where(loop => loop.Condition is null))
                findings.Add(At(loop, "JS004: for loops require an explicit condition."));
        }
        findings.AddRange(await CognitiveFindingsAsync(trees, cancellationToken));
        return findings;
    }

    private static bool IsFunction(SyntaxNode node) => node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax
        or AnonymousFunctionExpressionSyntax or AccessorDeclarationSyntax || node is PropertyDeclarationSyntax { ExpressionBody: not null };

    private static void InspectFunction(SyntaxNode function, List<string> findings)
    {
        var span = function.GetLocation().GetLineSpan();
        var lines = span.EndLinePosition.Line - span.StartLinePosition.Line + 1;
        var statements = function.DescendantNodes().OfType<StatementSyntax>().Count(statement => statement is not BlockSyntax);
        if (lines > 120) findings.Add(At(function, $"JS001: function has {lines} physical lines; maximum 120."));
        if (statements > 60) findings.Add(At(function, $"JS002: function has {statements} statements; maximum 60."));
        if (function is MethodDeclarationSyntax method && method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Any(call => call.Expression is IdentifierNameSyntax name && name.Identifier.ValueText == method.Identifier.ValueText))
            findings.Add(At(method, "JS005: direct unqualified recursion requires redesign."));
    }

    private static async Task<IEnumerable<string>> CognitiveFindingsAsync(IEnumerable<SyntaxTree> trees, CancellationToken cancellationToken)
    {
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty).Split(Path.PathSeparator)
            .Where(File.Exists).Select(path => MetadataReference.CreateFromFile(path));
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic> { ["S3776"] = ReportDiagnostic.Error });
        var compilation = CSharpCompilation.Create("JellySinPolicyAnalysis", trees, references, options);
        var additional = new AnalyzerOptions([new ConfigurationFile(Path.Combine(AppContext.BaseDirectory, "SonarLint.xml"))]);
        var analyzerOptions = new CompilationWithAnalyzersOptions(additional, onAnalyzerException: null, concurrentAnalysis: true, logAnalyzerExecutionTime: false);
        var diagnostics = await compilation.WithAnalyzers([new CognitiveComplexity { Threshold = 30, PropertyThreshold = 30 }], analyzerOptions)
            .GetAnalyzerDiagnosticsAsync(cancellationToken);
        return diagnostics.Where(item => item.Id is "S3776" or "AD0001" || item.Severity == DiagnosticSeverity.Error).Select(item => item.ToString());
    }

    private static async Task<int> SelfTestAsync(CancellationToken cancellationToken)
    {
        var small = CSharpSyntaxTree.ParseText("public class Valid { public int Value(bool x) { if (x) return 1; return 0; } }", cancellationToken: cancellationToken);
        if ((await InspectAsync([small], cancellationToken)).Count != 0) return Fail("Valid code was rejected.");
        var oversized = "public class Big { public void M() {" + string.Concat(Enumerable.Repeat("System.Console.WriteLine(1);\n", 121)) + "} }";
        var sizeFindings = await InspectAsync([CSharpSyntaxTree.ParseText(oversized, cancellationToken: cancellationToken)], cancellationToken);
        if (!sizeFindings.Any(item => item.Contains("JS001", StringComparison.Ordinal)) || !sizeFindings.Any(item => item.Contains("JS002", StringComparison.Ordinal))) return Fail("Size limits were not enforced.");
        var nested = "public class Complex { public void M(bool b) {" + string.Concat(Enumerable.Repeat("if(b) {", 9)) + "System.Console.WriteLine(1);" + new string('}', 11);
        var complexityFindings = await InspectAsync([CSharpSyntaxTree.ParseText(nested, cancellationToken: cancellationToken)], cancellationToken);
        if (!complexityFindings.Any(item => item.Contains("S3776", StringComparison.Ordinal) && item.Contains("30 allowed", StringComparison.Ordinal))) return Fail("Sonar S3776 threshold 30 was not enforced.");
        var loopFindings = await InspectAsync([CSharpSyntaxTree.ParseText("class Invalid { void M() { while(true) { break; } for(;;) { break; } goto end; end: M(); } }", cancellationToken: cancellationToken)], cancellationToken);
        if (new[] { "JS003", "JS004", "JS005" }.Any(rule => !loopFindings.Any(item => item.Contains(rule, StringComparison.Ordinal)))) return Fail("Control-flow rules were not enforced.");
        Console.WriteLine("Code policy self-tests passed, including official Sonar cognitive-complexity enforcement.");
        return 0;
    }

    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
    private static string At(SyntaxNode node, string message) => $"{node.SyntaxTree.FilePath}({node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}): {message}";

    private sealed class ConfigurationFile(string path) : AdditionalText
    {
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(File.ReadAllText(path));
    }
}
