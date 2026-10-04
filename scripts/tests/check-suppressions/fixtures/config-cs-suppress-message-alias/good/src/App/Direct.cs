using System.Diagnostics.CodeAnalysis;

namespace App;

/// <summary>Applies <see cref="SuppressMessageAttribute"/> directly, with its reason.</summary>
[SuppressMessage("Usage", "CA2200", Justification = "The original stack is logged before the rethrow.")]
public class Direct { }
