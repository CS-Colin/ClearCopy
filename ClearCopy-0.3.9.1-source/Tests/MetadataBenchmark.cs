using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using ClearCopy;

class MetadataBenchmark
{
    static int Main(string[] args)
    {
        RuntimeSetup.EnableLongPaths();
        string root = Path.GetFullPath(args[0]);
        string source = Path.Combine(root, "source", "a", "b", "c", "d", "e", "f");
        string target = Path.Combine(root, "target");
        Directory.CreateDirectory(@"\\?\" + source);
        for (int i = 0; i < 128; i++) File.WriteAllText(@"\\?\" + Path.Combine(source, i + ".txt"), new string('x', 1024));
        string file = Path.Combine(source, "0.txt");
        for (int i = 0; i < 100; i++) { Stamp.Read(file); CopyEngine.SafePath(file); }
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < 5000; i++) Stamp.Read(file);
        Console.WriteLine("5000 metadata reads: " + timer.ElapsedMilliseconds + " ms");
        timer.Restart();
        for (int i = 0; i < 500; i++) CopyEngine.SafePath(file);
        Console.WriteLine("500 full path safety checks: " + timer.ElapsedMilliseconds + " ms");
        timer.Restart();
        var engine = new CopyEngine();
        var result = engine.Run(engine.Plan(new[] { source }, target, CancellationToken.None, true), ExistingRule.Replace, true, false, CancellationToken.None);
        Console.WriteLine("128 small files copied and verified: " + timer.ElapsedMilliseconds + " ms; copied=" + result.Copied + "; matched=" + result.Matched + "; errors=" + result.Errors);
        return result.Copied == 128 && result.Matched == 128 && result.Errors == 0 ? 0 : 1;
    }
}
