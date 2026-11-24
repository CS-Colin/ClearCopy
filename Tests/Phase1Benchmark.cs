using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ClearCopy;

class Phase1Benchmark
{
    static int Main(string[] args)
    {
        RuntimeSetup.EnableLongPaths();
        string root = Path.GetFullPath(args[0]);
        if (root.StartsWith("\\\\") || Directory.Exists(root)) throw new Exception("New local benchmark directory required");
        Directory.CreateDirectory(root);
        AppDomain.MonitoringIsEnabled = true;
        string source = Path.Combine(root, "source"), target = Path.Combine(root, "target");
        Directory.CreateDirectory(source);
        int count = int.Parse(args[1]);
        for (int i = 0; i < count; i++) {
            string folder = Path.Combine(source, "d" + (i % 8)); Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, i.ToString("D6") + ".txt");
            File.WriteAllText(file, new string('x', 1024), new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(file, new DateTime(2020,1,1,0,0,0,DateTimeKind.Utc));
        }
        Result first = null;
        foreach (string scenario in new[] { "new-verified", "checkpoint-verified", "policy-fast" }) {
            var engine = new CopyEngine { Retries = 0 };
            if (scenario == "checkpoint-verified") engine.ResumeHashes = first.Items.Where(x => x.Status == "Matched").ToDictionary(x => x.Target, x => x.Hash);
            using (var log = SafeLogFile.Open(Path.Combine(root, scenario + ".log"))) {
                engine.Log = log.WriteLine;
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
                int gen2 = GC.CollectionCount(2); Update last = null; engine.Progress = u => last = u;
                var clock = Stopwatch.StartNew();
                var result = engine.Run(engine.Plan(new[] { source }, target, CancellationToken.None, true), ExistingRule.SkipSameAndOlder, scenario != "policy-fast", false, CancellationToken.None);
                log.Dispose(); clock.Stop();
                if (result.Errors != 0 || result.Mismatched != 0 || (scenario != "policy-fast" && result.Matched != count)) throw new Exception("Invalid benchmark result");
                if (scenario == "new-verified") first = result;
                Console.WriteLine(string.Join(",", scenario, count, clock.Elapsed.TotalMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before, GC.CollectionCount(2) - gen2, last.ReadBytes, result.Copied, result.Skipped, result.Matched));
            }
        }
        return 0;
    }
}
