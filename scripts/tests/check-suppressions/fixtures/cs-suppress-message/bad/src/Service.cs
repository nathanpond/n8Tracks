using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Design", "CA1014")]

namespace Fixture;

public class Service
{
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public int One() => 1;

    [SuppressMessage(
        "Style",
        "IDE0060:Remove unused parameter",
        Justification = "")]
    public int Two(int unused) => 2;

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = Reasons.Trimming)]
    public int Three() => 3;

    [System.Diagnostics.CodeAnalysis.SuppressMessageAttribute("Naming", "CA1707")]
    public int Four_Five() => 4;
}
