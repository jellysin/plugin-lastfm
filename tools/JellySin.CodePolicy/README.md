# C# code policy

```sh
dotnet run --project tools/JellySin.CodePolicy -c Release -- --self-test
dotnet run --project tools/JellySin.CodePolicy -c Release --no-build -- src
```

The tool parses production C# using the Roslyn assemblies supplied by the pinned
.NET SDK. It enforces a maximum of 120 physical lines and 60 statements for
methods, constructors, operators, accessors, local functions, lambdas and
expression-bodied properties. Nested statements count toward their enclosing
function; block delimiters do not count as statements. It rejects `goto`,
`while(true)`, condition-free `for` loops and direct unqualified recursion.

These syntax checks do not prove that every loop terminates. Work bounds,
cancellation, queue capacity and remote pagination remain explicit runtime
requirements verified by behavioral tests and review.

Cognitive complexity is measured by the official SonarAnalyzer.CSharp S3776
analyzer, pinned in the tool's project/lockfile, with threshold 30. The tool
instantiates that analyzer and explicitly enables its diagnostic; `SonarLint.xml`
sets method and property thresholds. A self-test intentionally exceeds the
threshold and fails unless Sonar emits S3776 with the configured limit. This is
the published metric, not a home-grown estimate.

The CLI is bounded to 2,048 files, 2 MB per file and two minutes per invocation.
Generated `obj`/`bin` files are excluded; production files are not selectively
excluded. Compiler diagnostics and the SDK's normal analyzers run separately in
the build. Roslyn syntax analysis here does not replace a successful build.

Dependency decision: a pinned build-only Sonar analyzer gives the established
cognitive-complexity metric and its language support. Reimplementing the metric
would introduce semantic drift and additional maintenance. The analyzer is not
linked into or shipped with the Jellyfin plugin. Its own license remains in its
NuGet package; JellySin's license applies to this newly written harness.

Primary references:

- [Official cognitive-complexity rule](https://rules.sonarsource.com/csharp/RSPEC-3776/)
- [Analyzer implementation and parameters](https://github.com/SonarSource/sonar-dotnet/blob/master/analyzers/src/SonarAnalyzer.Core/Rules/CognitiveComplexityBase.cs)
- [Pinned analyzer release](https://github.com/SonarSource/sonar-dotnet/releases/tag/10.33.0.1635)
