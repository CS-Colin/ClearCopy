using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using ClearCopy;

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
            var window = new MainWindow(new string[0]);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var windowType = typeof(MainWindow);
            var save = windowType.GetMethod("SaveCheckpoint", flags);
            var queue = windowType.GetMethod("QueueResult", flags);
            var last = windowType.GetField("lastCheckpoint", flags);
            var taskField = windowType.GetField("checkpointTask", flags);
            // 0.3.7: periodic checkpoints are written on a background thread.
            Action drain = () => ((Task)taskField.GetValue(window)).Wait();
            var job = new JobRecord { Sources = new[] { Path.Combine(root, "source") }, Destination = Path.Combine(root, "target") };
            windowType.GetField("currentJob", flags).SetValue(window, job);
            windowType.GetField("currentJobFile", flags).SetValue(window, file);
            save.Invoke(window, new object[] { true });
            if (Jobs.Load(file).Completed.Count != 0) return 1;
            last.SetValue(window, DateTime.UtcNow.AddSeconds(-5));
            queue.Invoke(window, new object[] { new ItemResult { Source = job.Sources[0], Target = job.Destination, Status = "Matched" } });
            if (job.Completed.Count != 1 || Jobs.Load(file).Completed.Count != 0) return 2;
            last.SetValue(window, DateTime.UtcNow.AddSeconds(-31));
            queue.Invoke(window, new object[] { new ItemResult { Source = job.Sources[0], Target = job.Destination, Status = "Matched" } });
            drain();
            if (Jobs.Load(file).Completed.Count != 2) return 3;
            queue.Invoke(window, new object[] { new ItemResult { Source = job.Sources[0], Target = job.Destination, Status = "Error" } });
            save.Invoke(window, new object[] { true });
            if (Jobs.Load(file).Completed.Count != 3) return 4;
            using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                last.SetValue(window, DateTime.UtcNow.AddSeconds(-31));
                save.Invoke(window, new object[] { false });
                drain();
                if ((DateTime.UtcNow - (DateTime)last.GetValue(window)).TotalSeconds > 2) return 5;
                if (string.IsNullOrEmpty((string)windowType.GetField("checkpointWarning", flags).GetValue(window))) return 6;
            }
            save.Invoke(window, new object[] { true });
            if (Jobs.Load(file).Completed.Count != 3) return 7;

            // 0.3.7 fix 2: a periodic save of a large job must not block the
            // thread that queues results (the copy worker).
            windowType.GetField("checkpointWarning", flags).SetValue(window, null);
            for (int i = 0; i < 300000; i++) job.Completed.Add(new ItemResult { Source = job.Sources[0] + "\\f" + i, Target = job.Destination + "\\f" + i, Status = "Matched", Reason = "SHA-256 primary streams matched", Hash = "AB-CD-EF" });
            var syncTimer = Stopwatch.StartNew();
            save.Invoke(window, new object[] { true });
            syncTimer.Stop();
            last.SetValue(window, DateTime.UtcNow.AddSeconds(-31));
            var queueTimer = Stopwatch.StartNew();
            queue.Invoke(window, new object[] { new ItemResult { Source = job.Sources[0], Target = job.Destination, Status = "Matched" } });
            queueTimer.Stop();
            Console.WriteLine("INFO 300k-row checkpoint: synchronous save " + syncTimer.ElapsedMilliseconds + " ms; worker blocked " + queueTimer.ElapsedMilliseconds + " ms with background save");
            if (queueTimer.ElapsedMilliseconds * 5 > syncTimer.ElapsedMilliseconds && queueTimer.ElapsedMilliseconds > 250) return 8;
            drain();
            if (Jobs.Load(file).Completed.Count != job.Completed.Count) return 9;

            // Results keep arriving from another thread while background saves run.
            int expected = job.Completed.Count + 20000;
            var producer = Task.Run(() =>
            {
                for (int i = 0; i < 20000; i++)
                {
                    if (i % 2000 == 0) last.SetValue(window, DateTime.UtcNow.AddSeconds(-31));
                    queue.Invoke(window, new object[] { new ItemResult { Source = "s" + i, Target = "t" + i, Status = "Copied" } });
                }
            });
            producer.Wait();
            drain();
            save.Invoke(window, new object[] { true });
            if (job.Completed.Count != expected || Jobs.Load(file).Completed.Count != expected) return 11;
            if (!string.IsNullOrEmpty((string)windowType.GetField("checkpointWarning", flags).GetValue(window))) return 12;
            Console.WriteLine("PASS: checkpoint throttling, periodic save, forced save, reload, failed-save backoff and recovery, background save does not block the worker, concurrent results");
            window.Close();
            return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 10; }
    }
}
