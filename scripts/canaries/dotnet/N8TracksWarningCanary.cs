// A canary for scripts/check-canaries.sh. It is never part of the product: the check copies it
// into a temporary copy of each project and requires the build to fail on the marked lines,
// with exactly these diagnostics, as errors. Each marker is "canary:" and the diagnostic's id.
namespace N8TracksWarningCanary;

internal sealed class WarningCanary
{
    public string NeverAssigned { get; set; } // canary: CS8618

    public static void Rethrow(System.Action action)
    {
        try
        {
            action();
        }
        catch (System.InvalidOperationException exception)
        {
            System.Console.Error.WriteLine(exception.Message);
            throw exception; // canary: CA2200
        }
    }
}
