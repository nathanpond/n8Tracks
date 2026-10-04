namespace Lib.Checks;

public static class GreeterCheck
{
    public static bool Passes() => Greeter.Greet("canary").Length > 0;
}
