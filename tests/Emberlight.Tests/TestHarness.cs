namespace Emberlight.Tests;

/// <summary>Tiny dependency-free test runner (no NuGet access needed). Exit code is the number of failures.</summary>
internal static class T
{
    private static int _pass, _fail;
    private static string _group = "";

    public static void Group(string name) { _group = name; Console.WriteLine($"\n{name}"); }

    public static void Run(string name, Action body)
    {
        try { body(); _pass++; Console.WriteLine($"  ok    {name}"); }
        catch (Exception ex)
        {
            _fail++;
            Console.WriteLine($"  FAIL  {name}\n        {ex.GetType().Name}: {ex.Message.Replace("\n", "\n        ")}");
        }
    }

    public static int Summary()
    {
        Console.WriteLine($"\n{_pass} passed, {_fail} failed");
        return _fail;
    }

    public static void True(bool cond, string? msg = null)
    {
        if (!cond) throw new Exception(msg ?? "expected true");
    }

    public static void Eq<TV>(TV expected, TV actual, string? msg = null)
    {
        if (!EqualityComparer<TV>.Default.Equals(expected, actual))
            throw new Exception($"{msg ?? "values differ"}: expected <{expected}> but was <{actual}>");
    }

    public static void SeqEq<TV>(IEnumerable<TV> expected, IEnumerable<TV> actual, string? msg = null)
    {
        var e = expected.ToList(); var a = actual.ToList();
        if (!e.SequenceEqual(a))
            throw new Exception($"{msg ?? "sequences differ"}: expected [{string.Join(", ", e)}] but was [{string.Join(", ", a)}]");
    }

    public static void Throws<TEx>(Action a) where TEx : Exception
    {
        try { a(); }
        catch (TEx) { return; }
        throw new Exception($"expected {typeof(TEx).Name}");
    }
}

/// <summary>Creates a scratch directory that is deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "phxslides-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public string Write(string name, byte[] bytes)
    {
        string p = File(name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllBytes(p, bytes);
        return p;
    }
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}
