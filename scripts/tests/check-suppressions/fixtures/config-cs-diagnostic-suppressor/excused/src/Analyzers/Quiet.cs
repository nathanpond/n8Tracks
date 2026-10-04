using Microsoft.CodeAnalysis.Diagnostics;

namespace Fixture.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class Quiet : DiagnosticSuppressor // Agreed with the team: the legacy module is too noisy to fix this quarter.
{
}
