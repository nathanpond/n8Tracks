namespace App;

[System.Diagnostics.CodeAnalysis.\U00000053uppressMessage("Usage", "CA2200", Justification = "The stack trace is reset on purpose here.")]
public class Escaped { }

// The stack trace is reset on purpose in this class.
[System.Diagnostics.CodeAnalysis.Suppress\U0000004DessageAttribute("Usage", "CA2200")]
public class EscapedTwice { }
