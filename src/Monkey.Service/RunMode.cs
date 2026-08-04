namespace Monkey.Service;

/// <summary>
/// Schalter, die es nur im Konsolenbetrieb gibt. Der installierte Dienst laeuft
/// nie hierdurch: Program.cs setzt sie ausschliesslich, wenn der Prozess nicht als
/// Windows-Dienst gestartet wurde.
/// </summary>
internal static class RunMode
{
    /// <summary>Abmeldung nur protokollieren statt ausfuehren.</summary>
    public static bool DryRunLogoff { get; set; }
}
