using Microsoft.CodeAnalysis.Diagnostics;

namespace Fixture.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class Quiet : DiagnosticSuppressor
{
}
