namespace Fixture;

public static class Widget
{
#pragma warning disable CS0618
    public static int Old() => Legacy.Value();
#pragma warning restore CS0618

    public static int Two()
    {
    #pragma warning disable CA1822, IDE0051
        return 2;
    }
}
