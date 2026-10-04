using System.Diagnostics.CodeAnalysis;
using Quiet = System.Diagnostics.CodeAnalysis.SuppressMessageAttribute;

namespace App;

[Quiet("Usage", "CA2200")]
public class Aliased
{
    private static readonly System.Type Marker = typeof(UnconditionalSuppressMessageAttribute);
}
