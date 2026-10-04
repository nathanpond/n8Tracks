using System.Diagnostics.CodeAnalysis;

namespace App;

/**/ [SuppressMessage("Usage", "CA2200")]
public class Hidden { }

/* the rule is noisy */ [SuppressMessage("Usage", "CA1822")]
public class HiddenToo
{
    /*
     */ [SuppressMessage("Usage", "CA1000")]
    public void Run() { }
}
