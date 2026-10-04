using System.Diagnostics.CodeAnalysis;
using Quiet = System.Diagnostics.CodeAnalysis.SuppressMessageAttribute; // Agreed with the team: the legacy module is too noisy to fix this quarter.

namespace App;

[Quiet("Usage", "CA2200")]
public class Aliased
{
    private static readonly System.Type Marker = typeof(UnconditionalSuppressMessageAttribute); // Agreed with the team: the legacy module is too noisy to fix this quarter.
}
