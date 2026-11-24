using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using ClearCopy;

// 0.4: large jobs. There is no periodic progress checkpoint any more; every
// result row is streamed to <log>.results.csv, and the window and job file keep
// all issue rows plus only the first resultRowLimit ordinary rows.
class CheckpointCheck
{
    [STAThread]
    static int Main(string[] args)
    {
        RuntimeSetup.EnableLongPaths();
        try
        {
            new Application();
            string root = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "checkpoint.xml");
            string log = Path.Combine(root, "job.log");
            var window = new MainWindow(new string[0]);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var windowType = typeof(MainWindow);
            var save = windowType.GetMethod("SaveCheckpoint", flags);
            var queue = windowType.GetMethod("QueueResult", flags);
            var last = windowType.GetField("lastCheckpoint", flags);
            var taskField = windowType.GetField("checkpointTask", flags);
            var limitField = windowType.GetField("resultRowLimit", flags);
            var streamField = windowType.GetField("resultsStream", flags);
            var droppedField = windowType.GetField("droppedRows", flags);
            Action drain = () => ((Task)taskField.GetValue(window)).Wait();
            const int limit = 1000;
            limitField.SetValue(window, limit);
            var job = new JobRecord { Sources = new[] { Path.Combine(root, "source") }, Destination = Path.Combine(root, "target") };
            windowType.GetField("currentJob", flags).SetValue(window, job);
            windowType.GetField("currentJobFile", flags).SetValue(window, file);
            var stream = new ResultsStream(log + ".results.csv");
            streamField.SetValue(window, stream);

            // The job file is written at start (forced save) ...
            save.Invoke(window, new object[] { true });
            if (Jobs.Load(file).Completed.Count != 0) return 1;
            int writesBefore = (int)windowType.GetField("CheckpointWrites").GetValue(window);

            // ... but no longer while results arrive, however old the last save is.
            last.SetValue(window, DateTime.UtcNow.AddMinutes(-10));
            queue.Invoke(window, new object[] { new ItemResult { Source = job.Sources[0], Target = job.Destination, Status = "Matched" } });
            drain();
            if (job.Completed.Count != 1) return 2;
            if ((int)windowType.GetField("CheckpointWrites").GetValue(window) != writesBefore || Jobs.Load(file).Completed.Count != 0) return 3;

            // 300,000 ordinary rows plus 50 problems from the worker thread:
            // memory keeps the first 1,000 ordinary rows and every problem;
            // the stream gets every row.
            long memoryBefore = GC.GetTotalMemory(true);
            var timer = Stopwatch.StartNew();
            var producer = Task.Run(() =>
            {
                for (int i = 0; i < 300000; i++)
                {
                    queue.Invoke(window, new object[] { new ItemResult { Source = job.Sources[0] + "\\f" + i, Target = job.Destination + "\\f" + i, Status = "Matched", Reason = "SHA-256 primary streams matched", Hash = "AB-CD-EF" } });
                    if (i % 6000 == 0) queue.Invoke(window, new object[] { new ItemResult { Source = job.Sources[0] + "\\e" + i, Target = job.Destination + "\\e" + i, Status = "Error", Reason = "Access denied" } });
                }
            });
            producer.Wait();
            timer.Stop();
            long memoryAfter = GC.GetTotalMemory(true);
            Console.WriteLine("INFO 300,050 rows queued in " + timer.ElapsedMilliseconds + " ms; managed memory grew " + ((memoryAfter - memoryBefore) / 1048576.0).ToString("F1") + " MB");
            if (job.Completed.Count != limit + 50) return 4;
            if (job.Completed.Count(Jobs.IsIssue) != 50) return 5;
            if ((long)droppedField.GetValue(window) != 300001 - limit) return 6;
            if (stream.Rows != 300051 || stream.Failed) return 7;
            if (memoryAfter - memoryBefore > 64L * 1048576) return 8;

            // A forced save (finish/cancel) holds only the kept rows.
            save.Invoke(window, new object[] { true });
            if (Jobs.Load(file).Completed.Count != limit + 50) return 9;

            // The finished report does not overwrite the full streamed list,
            // and the issues file still lists every problem.
            stream.Dispose();
            Jobs.WriteReport(log, job, "summary", "outcome", resultsStreamed: true);
            string[] lines = File.ReadAllLines(log + ".results.csv");
            if (lines.Length != 300052 || lines[0] != Jobs.CsvHeader) return 11;
            if (lines.Count(l => l.StartsWith("\"Error\",")) != 50) return 12;
            if (File.ReadAllLines(log + ".issues.csv").Length != 51) return 13;
            if (!File.ReadAllText(log + ".summary.txt").Contains(log + ".results.csv")) return 14;

            // Without streaming (stream failed), the report falls back to the kept rows.
            string log2 = Path.Combine(root, "fallback.log");
            Jobs.WriteReport(log2, job, "summary", "outcome");
            if (File.ReadAllLines(log2 + ".results.csv").Length != limit + 51) return 15;

            // The sync flow restarts the list before its final comparison.
            var resetStream = new ResultsStream(Path.Combine(root, "reset.csv"));
            for (int i = 0; i < 10; i++) resetStream.Write(new ItemResult { Source = "a" + i, Target = "b" + i, Status = "Copied" });
            resetStream.Reset();
            resetStream.Write(new ItemResult { Source = "c", Target = "d", Status = "Matched" });
            resetStream.Dispose();
            string[] reset = File.ReadAllLines(Path.Combine(root, "reset.csv"));
            if (reset.Length != 2 || !reset[1].StartsWith("\"Matched\",")) return 16;

            // A list that cannot be written reports a warning instead of stopping the job.
            var broken = new ResultsStream(Path.Combine(root, "missing-folder", "x.csv"));
            broken.Write(new ItemResult { Source = "a", Target = "b", Status = "Copied" });
            if (!broken.Failed || string.IsNullOrEmpty(broken.Warning)) return 17;
            broken.Dispose();

            Console.WriteLine("PASS: no periodic checkpoint, forced save holds kept rows, 300k rows capped in memory, every row streamed, report keeps the full list, sync reset, unwritable list warning");
            window.Close();
            return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 10; }
    }
}
