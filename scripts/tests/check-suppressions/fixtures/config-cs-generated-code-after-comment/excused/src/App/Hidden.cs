using System.CodeDom.Compiler;
using System.Diagnostics.CodeAnalysis;

namespace App;

/* This class is written by a tool, so analysis is off. */ [GeneratedCode("tool", "1.0")]
public class Hidden { }

/* This class is written by a tool as well. */ [GeneratedCodeAttribute("tool", "1.0")]
public class HiddenToo
{
    /* The alias is needed by the tool's output. */ private static readonly System.Type Alias = typeof(SuppressMessageAttribute);
}
