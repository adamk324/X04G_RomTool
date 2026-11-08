namespace X04G.Core.Common;

/// <summary>Overall result of a check. Ordered so that Max() gives the worst result.</summary>
public enum Verdict { Pass = 0, Warning = 1, Fail = 2 }

public enum Severity { Info, Pass, Warning, Fail }

public sealed record Finding(Severity Severity, string Category, string Message)
{
    public override string ToString() => $"[{Severity.ToString().ToUpperInvariant()}] {Category}: {Message}";
}

public static class Findings
{
    /// <summary>PASS only when nothing is uncertain; any warning ⇒ WARNING; any failure ⇒ FAIL. An empty list is a WARNING (nothing verified).</summary>
    public static Verdict Combine(IEnumerable<Finding> findings)
    {
        var list = findings.ToList();
        if (list.Any(f => f.Severity == Severity.Fail)) return Verdict.Fail;
        if (list.Any(f => f.Severity == Severity.Warning)) return Verdict.Warning;
        if (!list.Any(f => f.Severity == Severity.Pass)) return Verdict.Warning;
        return Verdict.Pass;
    }
}
