namespace MiniCore.Compare.Interop.Adapters;

public static class ComparisonStackFactory
{
    public static IComparisonStack Create(StackKind kind) => kind switch
    {
        StackKind.Callora => new CalloraStack(),
        StackKind.SipSorcery => new SipSorceryStack(),
        StackKind.Ozeki => new OzekiStack(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown VoIP stack."),
    };
}
