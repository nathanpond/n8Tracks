using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Design", "CA1014", Justification = "Not a public library: nothing consumes it from another language.")]

namespace Fixture;

// The attribute name alone is not a suppression: [SuppressMessage("x", "y")] in a comment.
public class Service
{
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Part of the instance contract (IService).")]
    public int One() => 1;

    [SuppressMessage(
        "Style",
        "IDE0060:Remove unused parameter",
        Justification = "The delegate signature " +
            "is fixed by the framework.")]
    public int Two(int unused) => 2;

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = @"The type is rooted in ""Roots.xml"".")]
    public int Three() => 3;

    // Test names use underscores to read as sentences.
    [SuppressMessage("Naming", "CA1707")]
    public int Four_Five() => 4;

    [SuppressMessage("Naming", "CA1707")] // Test names use underscores to read as sentences.
    public int Six_Seven() => 6;

    public string Text() => "a string with ) and \" in it";
}
