// Copied from the vendor's sample, which predates nullable reference types.
#nullable disable

namespace Fixture;

public class Legacy
{
    public string Name;
#nullable disable warnings // The deserializer fills this in after construction.
    public string Other;
#nullable restore
#nullable enable
}
