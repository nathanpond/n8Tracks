namespace Fixture;

public static class Widget
{
    // The replacement API ships in the next SDK; until then the obsolete call is the only one.
#pragma warning disable CS0618
    public static int Old() => Legacy.Value();
#pragma warning restore CS0618

    public static int Two()
    {
    #pragma warning disable CA1822 // Kept an instance member for the serializer's reflection.
        return 2;
    }

    /*
     * The generated parser trips the analyzer on every switch arm,
     * and the generator is not ours to change.
     */
#pragma warning disable CA1502
    public static int Three() => 3;
}
