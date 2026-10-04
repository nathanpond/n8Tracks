namespace Fixture;

// TODO: explain later
#pragma warning disable CS0618
#pragma warning disable CS0612 // FIXME
// pending review by the team lead
#nullable disable
// N/A for this file, honestly
#pragma warning disable CA1000
// too short
#pragma warning disable CA1001
#pragma warning disable CA1002 //
// Todo - write the real reason here
#pragma warning disable CA1003
// The first directive is explained by this comment well enough.
#pragma warning disable CA1004
#pragma warning disable CA1005

public class Placeholders
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1006", Justification = "<Pending>")]
    public int One() => 1;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1007", Justification = "")]
    public int Two() => 2;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1008", Justification = "todo: say why this is fine")]
    public int Three() => 3;

    // A blank line separates this comment from the directive, so it explains nothing.

#pragma warning disable CA1009
}
