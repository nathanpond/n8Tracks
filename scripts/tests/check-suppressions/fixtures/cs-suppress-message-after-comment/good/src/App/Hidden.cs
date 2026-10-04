using System.Diagnostics.CodeAnalysis;

namespace App;

/**/ [SuppressMessage("Usage", "CA2200", Justification = "The stack trace is reset on purpose here.")]
public class Hidden { }

// [SuppressMessage("Usage", "CA1822")] was removed from this class.
/* [SuppressMessage("Usage", "CA1822")] */
public class HiddenToo
{
    private const string Slashes = "// not a comment"; private const char Quote = '"';
    /*
     * [SuppressMessage("Usage", "CA1000")]
     */
    public void Run() { }
}
