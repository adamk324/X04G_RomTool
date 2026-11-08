using System.Diagnostics;
using System.Reflection;

namespace X04G.Core.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute { }

public sealed class SkipException : Exception { public SkipException(string m) : base(m) { } }
public sealed class AssertException : Exception { public AssertException(string m) : base(m) { } }

public static class Assert
{
    public static void True(bool c, string msg = "expected true") { if (!c) throw new AssertException(msg); }
    public static void False(bool c, string msg = "expected false") { if (c) throw new AssertException(msg); }
    public static void Equal<T>(T expected, T actual, string? msg = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertException($"{msg ?? "values differ"}: expected <{expected}>, actual <{actual}>");
    }
    public static void SeqEqual(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, string msg = "byte sequences differ")
    {
        if (!a.SequenceEqual(b)) throw new AssertException($"{msg} (len {a.Length} vs {b.Length})");
    }
    public static T Throws<T>(Action a) where T : Exception
    {
        try { a(); }
        catch (T ex) { return ex; }
        catch (Exception ex) { throw new AssertException($"expected {typeof(T).Name}, got {ex.GetType().Name}: {ex.Message}"); }
        throw new AssertException($"expected {typeof(T).Name}, nothing was thrown");
    }
    public static void Contains(string needle, string hay) { if (!hay.Contains(needle)) throw new AssertException($"'{needle}' not found in: {hay}"); }
    public static void NotNull(object? o, string msg = "expected non-null") { if (o == null) throw new AssertException(msg); }
}

public static class Runner
{
    public static int Main(string[] args)
    {
        var filter = args.FirstOrDefault();
        var tests = typeof(Runner).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.GetCustomAttribute<TestAttribute>() != null).Select(m => (t, m)))
            .Where(x => filter == null || $"{x.t.Name}.{x.m.Name}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.t.Name).ThenBy(x => x.m.Name).ToList();
        int pass = 0, fail = 0, skip = 0;
        var sw = Stopwatch.StartNew();
        foreach (var (t, m) in tests)
        {
            var name = $"{t.Name}.{m.Name}";
            var one = Stopwatch.StartNew();
            try
            {
                m.Invoke(null, null);
                pass++;
                Console.WriteLine($"  PASS  {name} ({one.ElapsedMilliseconds} ms)");
            }
            catch (TargetInvocationException tie) when (tie.InnerException is SkipException s)
            {
                skip++;
                Console.WriteLine($"  SKIP  {name}: {s.Message}");
            }
            catch (TargetInvocationException tie)
            {
                fail++;
                var e = tie.InnerException!;
                Console.WriteLine($"  FAIL  {name}: {e.GetType().Name}: {e.Message}");
                if (e is not AssertException) Console.WriteLine(e.StackTrace);
            }
        }
        Console.WriteLine();
        Console.WriteLine($"{pass} passed, {fail} failed, {skip} skipped in {sw.Elapsed.TotalSeconds:0.0} s");
        Fixtures.Cleanup();
        return fail == 0 ? 0 : 1;
    }
}
