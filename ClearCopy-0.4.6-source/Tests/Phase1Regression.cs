using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClearCopy;

class Phase1Regression
{
    static string root;
    static int passed, failed;
    static readonly DateTime Old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    static readonly DateTime New = Old.AddDays(1);
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static void Test(string name, Action run)
    {
        try { run(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
    }
    static object Field(MainWindow w, string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(w);
    static void Invoke(MainWindow w, string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, null, args.Select(x => x.GetType()).ToArray(), null).Invoke(w, args);
    static MainWindow TestWindow(Fixture f)
    {
        var w = new MainWindow(new[] { "--test-no-integration" });
        typeof(MainWindow).GetField("automaticTest", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(w, true);
        typeof(MainWindow).GetField("testLogDirectory", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(w, Path.Combine(f.Dir, "ui-logs"));
        ((ListBox)Field(w, "sources")).Items.Add(f.Source);
        ((TextBox)Field(w, "destination")).Text = f.Target;
        ((CheckBox)Field(w, "directCompare")).IsChecked = true;
        return w;
    }
    static JobRecord WaitJob(MainWindow w)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) }; var start = DateTime.UtcNow;
        bool timedOut = false;
        timer.Tick += (_, __) => {
            if (!(bool)Field(w, "starting") && !(bool)Field(w, "busy") && Field(w, "currentJob") != null) frame.Continue = false;
            if ((DateTime.UtcNow-start).TotalSeconds > 30) { timedOut = true; frame.Continue = false; }
        };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Require(!timedOut, "UI job timed out"); return (JobRecord)Field(w, "currentJob");
    }
    static void Junction(string link, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var p = System.Diagnostics.Process.Start(psi)) { p.WaitForExit(); Require(p.ExitCode == 0 && Directory.Exists(link), "Could not create test junction: " + p.StandardError.ReadToEnd()); }
    }
    // 0.4.1: lock a test item so only SYSTEM may use it (the test account stays owner, so it can be undone).
    static void LockOut(string path)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (Directory.Exists(path))
        {
            var ds = new DirectorySecurity(); ds.SetAccessRuleProtection(true, false);
            ds.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(path, ds);
        }
        else
        {
            var fs = new FileSecurity(); fs.SetAccessRuleProtection(true, false);
            fs.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
            File.SetAccessControl(path, fs);
        }
    }
    static void Unlock(string path)
    {
        var me = WindowsIdentity.GetCurrent().User;
        try
        {
            if (Directory.Exists(path)) { var ds = new DirectorySecurity(); ds.SetAccessRuleProtection(false, false); ds.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow)); Directory.SetAccessControl(path, ds); }
            else { var fs = new FileSecurity(); fs.SetAccessRuleProtection(false, false); fs.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow)); File.SetAccessControl(path, fs); }
        }
        catch { }
    }
    static void PumpUntil(Func<bool> done, double seconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) }; var start = DateTime.UtcNow;
        timer.Tick += (_, __) => { if (done() || (DateTime.UtcNow - start).TotalSeconds > seconds) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
    }
    sealed class Fixture
    {
        public string Dir, Source, Target;
        public CopyEngine Engine = new CopyEngine { Retries = 0 };
        public Fixture()
        {
            Dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Source = Path.Combine(Dir, "source"); Target = Path.Combine(Dir, "target");
            Directory.CreateDirectory(Source); Directory.CreateDirectory(Target);
        }
        public string Put(bool target, string name, string contents, DateTime? time = null)
        {
            string path = Path.Combine(target ? Target : Source, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, contents, new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, time ?? Old);
            return path;
        }
        public List<Entry> Plan() => Engine.Plan(new[] { Source }, Target, CancellationToken.None, true);
        public Result Run(OperationMode mode, ExistingRule rule = ExistingRule.SkipSameAndOlder) => Engine.Run(Plan(), rule, mode, CancellationToken.None);
        public string Read(string name) => File.ReadAllText(Path.Combine(Target, name));
    }
    sealed class CountingWriter : StringWriter
    {
        public int Flushes;
        public bool ThrowOnFlush;
        public override void Flush() { Flushes++; if (ThrowOnFlush) throw new IOException("Injected log flush failure"); base.Flush(); }
    }

    [STAThread]
    static int Main(string[] args)
    {
        RuntimeSetup.EnableLongPaths();
        root = Path.GetFullPath(args[0]);
        if (root.StartsWith("\\\\") || Directory.Exists(root)) throw new Exception("Tests require a NEW disposable local directory");
        Directory.CreateDirectory(root);
        Test("Fast Copy new file, timestamp and explicit not-verified state", () => {
            var f = new Fixture(); var source = f.Put(false, "a", "hello"); int reads = 0;
            f.Engine.BeforeRead = _ => reads++;
            var r = f.Run(OperationMode.FastCopy);
            Require(r.Copied == 1 && r.Matched == 0 && r.Clean && reads == 0, "Unexpected verification/copy state");
            Require(f.Read("a") == "hello" && File.GetLastWriteTimeUtc(source) == File.GetLastWriteTimeUtc(Path.Combine(f.Target, "a")), "Data/time changed");
            Require(r.Items.Single(x => x.Status == "Copied").DisplayStatus == "Copied — Not Verified", "Display state");
        });
        Test("Fast Copy equal-size/time collision and checkpoint do not hash", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA"); var target = f.Put(true, "a", "BBBB");
            f.Engine.ResumeHashes[target] = "Historical hash must not be trusted";
            f.Engine.BeforeRead = _ => { throw new Exception("Unexpected hash read"); };
            var r = f.Run(OperationMode.FastCopy);
            Require(r.Skipped == 1 && r.Matched == 0 && f.Read("a") == "BBBB", "Policy skip certified or changed content");
            Require(r.Items.Any(x => x.DisplayStatus == "Policy Skipped — Not Verified"), "Skip state");
        });
        Test("Verified Copy identical files are hashed and matched", () => {
            var f = new Fixture(); f.Put(false, "a", "same"); f.Put(true, "a", "same");
            var r = f.Run(OperationMode.VerifiedCopy);
            Require(r.Skipped == 1 && r.Matched == 1 && r.Clean, "Identical file was not verified");
        });
        Test("Verified Copy detects same-size/time different content", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA"); f.Put(true, "a", "BBBB");
            var r = f.Run(OperationMode.VerifiedCopy);
            Require(r.Mismatched == 1 && r.Matched == 0 && !r.Clean && f.Read("a") == "BBBB", "Collision incorrectly matched");
        });
        Test("Configurable timestamp tolerance is policy only", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA", Old.AddSeconds(1)); f.Put(true, "a", "BBBB");
            f.Engine.SkipTimeTolerance = TimeSpan.FromSeconds(2);
            var r = f.Run(OperationMode.FastCopy);
            Require(r.Skipped == 1 && r.Matched == 0 && f.Read("a") == "BBBB", "Tolerance did not control skip");
        });
        Test("All write modes and overwrite rules protect newer destinations", () => {
            foreach (var mode in new[] { OperationMode.FastCopy, OperationMode.VerifiedCopy })
            foreach (ExistingRule rule in Enum.GetValues(typeof(ExistingRule))) {
                var f = new Fixture(); f.Put(false, "a", "source"); var dst = f.Put(true, "a", "newer", New);
                f.Engine.PreserveDetails = true;
                var r = f.Run(mode, rule);
                Require(r.Copied == 0 && r.ProtectedNewer == 1 && f.Read("a") == "newer" && File.GetLastWriteTimeUtc(dst) == New && !r.Clean, mode + " " + rule + " overwrote/certified newer data");
            }
        });
        Test("Explicit newer-file authorization retains recovery backup", () => {
            var f = new Fixture(); f.Put(false, "a", "source"); f.Put(true, "a", "newer", New); int prompts = 0;
            f.Engine.ConfirmNewerDestination = c => { prompts++; return c.Target == Path.Combine(f.Target, "a") && c.TargetWriteUtc == New; };
            var r = f.Run(OperationMode.VerifiedCopy, ExistingRule.Replace);
            Require(prompts == 1 && r.Copied == 1 && r.Matched == 1 && f.Read("a") == "source", "Confirmed replacement failed");
            string backup = r.Items.First(x => x.Status == "Copied").Backup;
            Require(File.ReadAllText(backup) == "newer", "Previous version not retained");
        });
        Test("Authorization is per conflict, not a blanket job permission", () => {
            var f = new Fixture(); foreach (string n in new[] { "a", "b" }) { f.Put(false, n, "source"); f.Put(true, n, "newer", New); }
            int prompts = 0; f.Engine.ConfirmNewerDestination = c => { prompts++; return Path.GetFileName(c.Target) == "a"; };
            var r = f.Run(OperationMode.FastCopy, ExistingRule.Replace);
            Require(prompts == 2 && r.Copied == 1 && r.ProtectedNewer == 1 && f.Read("b") == "newer", "Authorization leaked to another file");
        });
        Test("Destination changes during confirmation invalidate approval", () => {
            var f = new Fixture(); f.Put(false, "a", "source"); f.Put(true, "a", "newer", New);
            f.Engine.ConfirmNewerDestination = c => { File.WriteAllText(c.Target, "external newer change"); return true; };
            var r = f.Run(OperationMode.FastCopy, ExistingRule.Replace);
            Require(r.Copied == 0 && r.Changed > 0 && f.Read("a") == "external newer change", "Stale confirmation accepted");
        });
        Test("Destination changes at commit preserve concurrent writer", () => {
            var f = new Fixture(); f.Put(false, "a", "source"); f.Put(true, "a", "newer", New);
            f.Engine.ConfirmNewerDestination = _ => true;
            f.Engine.BeforeCommit = p => File.WriteAllText(p, "concurrent writer");
            var r = f.Run(OperationMode.VerifiedCopy, ExistingRule.Replace);
            Require(r.Copied == 0 && r.Changed > 0 && f.Read("a") == "concurrent writer", "Commit overwrote concurrent destination");
            Require(!Directory.GetFiles(f.Target, "*.partial").Any(), "Temporary file leaked");
        });
        Test("Overwrite hashes detect same-size/time concurrent content changes", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA", New); var dst = f.Put(true, "a", "BBBB");
            f.Engine.BeforeCommit = p => { File.WriteAllText(p, "CCCC"); File.SetLastWriteTimeUtc(p, Old); };
            var r = f.Run(OperationMode.FastCopy, ExistingRule.Replace);
            Require(r.Copied == 0 && r.Changed > 0 && f.Read("a") == "CCCC", "Overwrite protection hashing was weakened");
        });
        Test("Compare is read-only, detects missing/mismatch and keeps extras", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA"); f.Put(false, "missing", "x"); f.Put(true, "a", "BBBB"); var extra = f.Put(true, "extra", "keep");
            var r = f.Run(OperationMode.Compare, ExistingRule.Replace);
            Require(r.Mismatched == 1 && r.Missing == 1 && r.Copied == 0 && f.Read("a") == "BBBB" && File.Exists(extra) && !File.Exists(Path.Combine(f.Target, "missing")), "Compare wrote or missed differences");
        });
        Test("Directory creation optimization still rejects file/directory collisions", () => {
            var f = new Fixture(); f.Put(false, "nested/a", "source"); f.Put(true, "nested", "keep", New);
            var r = f.Run(OperationMode.FastCopy);
            Require(r.Errors > 0 && f.Read("nested") == "keep" && r.FoldersPresent == 1, "Destination file treated as a folder");
        });
        Test("Successful checkpoint revalidation performs one source/target pair", () => {
            var f = new Fixture(); f.Put(false, "a", "same"); f.Put(true, "a", "same");
            var first = f.Run(OperationMode.Compare); f.Engine.ResumeHashes = first.Items.Where(x => x.Status == "Matched").ToDictionary(x => x.Target, x => x.Hash);
            int reads = 0; f.Engine.BeforeRead = _ => reads++; Update last = null; f.Engine.Progress = u => last = u;
            var r = f.Run(OperationMode.VerifiedCopy);
            Require(reads == 2 && last.ReadBytes == 8 && r.Matched == 1 && r.Skipped == 1, "Duplicate pair or invalid resume");
        });
        Test("Checkpoint mismatch is never trusted", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA"); var dst = f.Put(true, "a", "BBBB"); f.Engine.ResumeHashes[dst] = "old-hash";
            var r = f.Run(OperationMode.VerifiedCopy);
            Require(r.Matched == 0 && r.Mismatched == 1 && f.Read("a") == "BBBB", "Stale checkpoint certified");
        });
        Test("Revalidated checkpoint never rewrites newer destination timestamps", () => {
            var f = new Fixture(); f.Put(false, "a", "same"); var dst = f.Put(true, "a", "same", New);
            var compare = f.Run(OperationMode.Compare);
            f.Engine.ResumeHashes = compare.Items.Where(x => x.Status == "Matched").ToDictionary(x => x.Target, x => x.Hash);
            f.Engine.PreserveDetails = true; var r = f.Run(OperationMode.VerifiedCopy);
            Require(r.Matched == 1 && File.GetLastWriteTimeUtc(dst) == New, "Checkpoint overwrote protected newer metadata");
        });
        Test("Repair copies missing file and SHA-256 verifies it", () => {
            var f = new Fixture(); f.Put(false, "nested/a", "repair me");
            var compare = f.Run(OperationMode.Compare); var candidates = Jobs.DifferenceEntries(f.Plan(), compare.Items);
            var r = f.Engine.Repair(candidates, CancellationToken.None);
            Require(r.Copied == 1 && r.Matched == 1 && r.Clean && f.Read("nested/a") == "repair me", "Missing repair failed");
        });
        Test("Repair recopies content mismatch even with equal size/time", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA"); f.Put(true, "a", "BBBB");
            var compare = f.Run(OperationMode.Compare);
            var r = f.Engine.Repair(Jobs.DifferenceEntries(f.Plan(), compare.Items), CancellationToken.None);
            Require(r.Copied == 1 && r.Matched == 1 && f.Read("a") == "AAAA", "Mismatch was skipped");
        });
        Test("Repair rechecks stale mismatch that now matches without writing", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA"); f.Put(true, "a", "BBBB"); var compare = f.Run(OperationMode.Compare);
            f.Put(true, "a", "AAAA"); var r = f.Engine.Repair(Jobs.DifferenceEntries(f.Plan(), compare.Items), CancellationToken.None);
            Require(r.Copied == 0 && r.Matched == 1 && !Directory.GetFiles(f.Target, "*.bak").Any(), "Unnecessary repair copy");
        });
        Test("Repair protects newer destination until individually confirmed", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA"); f.Put(true, "a", "BBBB", New); var compare = f.Run(OperationMode.Compare);
            var r = f.Engine.Repair(Jobs.DifferenceEntries(f.Plan(), compare.Items), CancellationToken.None);
            Require(r.Copied == 0 && r.ProtectedNewer == 1 && f.Read("a") == "BBBB", "Repair overwrote newer file");
            f.Engine.ConfirmNewerDestination = _ => true;
            r = f.Engine.Repair(Jobs.DifferenceEntries(f.Plan(), compare.Items), CancellationToken.None);
            Require(r.Copied == 1 && r.Matched == 1, "Confirmed repair did not verify");
        });
        Test("Metadata-only differences are separate and never content repair candidates", () => {
            var f = new Fixture(); f.Put(false, "a", "same"); var dst = f.Put(true, "a", "same"); File.SetAttributes(dst, FileAttributes.Hidden);
            f.Engine.PreserveDetails = true; var r = f.Run(OperationMode.Compare);
            Require(r.Matched == 1 && r.MetadataDifferences > 0 && Jobs.DifferenceEntries(f.Plan(), r.Items).Count == 0, "Metadata issue became content repair");
            Require((File.GetAttributes(dst) & FileAttributes.Hidden) != 0 && f.Read("a") == "same", "Compare changed metadata/content");
        });
        Test("Changed/inaccessible/metadata issues are excluded; later state wins", () => {
            var rows = new[] {
                new ItemResult { Source="s", Target="t", Status="Mismatch" }, new ItemResult { Source="s", Target="t", Status="Changed" },
                new ItemResult { Source="s2", Target="t2", Status="Error" }, new ItemResult { Source="s3", Target="t3", Status="Metadata difference" },
                new ItemResult { Source="s4", Target="t4", Status="Missing" }
            };
            Require(Jobs.RepairableRows(rows).Length == 1 && Jobs.RepairableRows(rows)[0].Target == "t4", "Unresolved errors became repairs");
        });
        Test("Metadata difference does not hide a simultaneous content mismatch", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA"); var dst = f.Put(true, "a", "BBBB"); File.SetAttributes(dst, FileAttributes.Hidden);
            f.Engine.PreserveDetails = true; var compare = f.Run(OperationMode.Compare);
            Require(compare.Mismatched == 1 && compare.MetadataDifferences > 0 && Jobs.RepairableRows(compare.Items).Length == 1, "Metadata hid content mismatch");
            var r = f.Engine.Repair(Jobs.DifferenceEntries(f.Plan(), compare.Items), CancellationToken.None);
            Require(r.Copied == 1 && r.Matched == 1 && f.Read("a") == "AAAA", "Combined issue not repaired");
        });
        Test("Locked destination produces unresolved failure without overwrite", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA"); string dst = f.Put(true, "a", "BBBB");
            using (new FileStream(dst, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                var r = f.Run(OperationMode.Compare);
                Require(r.Errors > 0 && r.Matched == 0 && Jobs.DifferenceEntries(f.Plan(), r.Items).Count == 0, "Locked file certified/repaired");
            }
            Require(f.Read("a") == "BBBB", "Locked file overwritten");
        });
        Test("Injected access denial is visible and excluded from repair", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA"); f.Put(true, "a", "AAAA"); f.Engine.BeforeRead = _ => { throw new UnauthorizedAccessException("Injected access denial"); };
            var r = f.Run(OperationMode.Compare);
            Require(r.Errors > 0 && r.Unchecked > 0 && r.Matched == 0 && Jobs.DifferenceEntries(f.Plan(), r.Items).Count == 0, "Access denial hidden");
        });
        Test("Cancellation removes partial, records not reached, then resumes safely", () => {
            var f = new Fixture(); f.Put(false, "a", new string('x', 2 * 1048576)); f.Put(false, "b", "later"); var plan = f.Plan(); var cts = new CancellationTokenSource(); var rows = new List<ItemResult>();
            f.Engine.ItemCompleted = rows.Add; f.Engine.Progress = u => { if (u.Phase == "Copying" && u.FileBytes > 0) cts.Cancel(); };
            bool cancelled = false; try { f.Engine.Run(plan, ExistingRule.Replace, OperationMode.VerifiedCopy, cts.Token); } catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled && rows.Any(x => x.Status == "Not reached") && !Directory.GetFiles(f.Target, "*.partial").Any(), "Cancellation cleanup/state failed");
            f.Engine.Progress = _ => { }; var r = f.Run(OperationMode.VerifiedCopy);
            Require(r.Matched == 2 && r.Clean, "Resume failed");
        });
        Test("Source modification before copying blocks stale plan", () => {
            var f = new Fixture(); string src = f.Put(false, "a", "AAAA"); f.Put(true, "a", "BBBB");
            f.Engine.BeforeCopy = _ => File.WriteAllText(src, "changed source");
            var r = f.Run(OperationMode.VerifiedCopy, ExistingRule.Replace);
            Require(r.Copied == 0 && r.Changed > 0 && f.Read("a") == "BBBB", "Changed source copied");
        });
        Test("Source modification after hashing is caught by final audit", () => {
            var f = new Fixture(); string src = f.Put(false, "a", "AAAA"); f.Put(true, "a", "AAAA");
            f.Engine.ItemCompleted = row => { if (row.Status == "Matched") File.WriteAllText(src, "changed after verification"); };
            var r = f.Run(OperationMode.Compare);
            Require(r.Changed > 0 && !r.Clean, "Final audit missed changed source");
        });
        Test("Existing metadata/DACL preservation remains supported", () => {
            var f = new Fixture(); var src = f.Put(false, "a", "meta"); File.SetCreationTimeUtc(src, Old.AddDays(-1)); File.SetAttributes(src, FileAttributes.Hidden | FileAttributes.Archive);
            f.Engine.PreserveDetails = true; f.Engine.PreservePermissions = true;
            var r = f.Run(OperationMode.FastCopy); Require(r.Copied == 1 && r.Errors == 0, "Metadata application failed");
            var compare = f.Run(OperationMode.Compare); Require(compare.Matched == 1 && compare.MetadataDifferences == 0 && compare.Errors == 0, "Metadata preservation regression");
        });
        Test("0.3.7 DACL compare ignores only the auto-inherited flags", () => {
            Require(CopyEngine.ComparableDacl("D:AI(A;ID;FA;;;SY)(A;ID;FA;;;BA)") == CopyEngine.ComparableDacl("D:(A;ID;FA;;;SY)(A;ID;FA;;;BA)"), "AI flag still reported as a difference");
            Require(CopyEngine.ComparableDacl("D:ARAI(A;;FA;;;SY)") == CopyEngine.ComparableDacl("D:(A;;FA;;;SY)"), "AR flag still reported as a difference");
            Require(CopyEngine.ComparableDacl("D:PAI(A;;FA;;;SY)") != CopyEngine.ComparableDacl("D:AI(A;;FA;;;SY)"), "Protected (inheritance disabled) flag ignored");
            Require(CopyEngine.ComparableDacl("D:AI(A;ID;FA;;;SY)") != CopyEngine.ComparableDacl("D:AI(A;ID;FA;;;BA)"), "Different ACE ignored");
            Require(CopyEngine.ComparableDacl("D:AI(A;ID;FA;;;SY)") != CopyEngine.ComparableDacl("D:AI(A;ID;0x1301ff;;;SY)"), "Different rights ignored");
            Require(CopyEngine.ComparableDacl("D:NO_ACCESS_CONTROL") == "D:NO_ACCESS_CONTROL", "Null DACL altered");
        });
        Test("Inherited Archive-bit edge case is visible in independent Compare", () => {
            var f = new Fixture(); var src = f.Put(false, "a", "archive-edge"); File.SetAttributes(src, FileAttributes.Hidden);
            f.Engine.PreserveDetails = true; f.Engine.PreservePermissions = true; f.Run(OperationMode.FastCopy);
            var compare = f.Run(OperationMode.Compare);
            bool sameAttributes = File.GetAttributes(src) == File.GetAttributes(Path.Combine(f.Target, "a"));
            Require(compare.Matched == 1 && (sameAttributes || compare.MetadataDifferences > 0 && !compare.Clean), "Metadata discrepancy was silently certified");
            Console.WriteLine("INFO cleared-Archive probe: attributes equal=" + sameAttributes + ", metadata differences=" + compare.MetadataDifferences + "; same limitation reproduced in original 0.3.5.4");
        });
        Test("Restrictive directory DACL is applied after child content copying", () => {
            var f = new Fixture(); f.Put(false, "a", "read-only source folder");
            var acl = Directory.GetAccessControl(f.Source);
            acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny));
            Directory.SetAccessControl(f.Source, acl);
            f.Engine.PreserveDetails = true; f.Engine.PreservePermissions = true;
            var r = f.Run(OperationMode.VerifiedCopy);
            Require(r.Copied == 1 && r.Matched == 1 && r.Errors == 0 && f.Read("a") == "read-only source folder", "Directory DACL applied before copying children");
        });
        Test("0.3.5.4 XML jobs map to explicit modes and retain checkpoints", () => {
            foreach (var x in new[] { new { Verify=true, Compare=false, Mode=OperationMode.VerifiedCopy }, new { Verify=false, Compare=false, Mode=OperationMode.FastCopy }, new { Verify=false, Compare=true, Mode=OperationMode.Compare } }) {
                string file = Path.Combine(root, Guid.NewGuid() + ".xml");
                File.WriteAllText(file, "<JobRecord><Schema>1</Schema><Build>0.3.5.4</Build><Sources><string>C:\\fixture\\source</string></Sources><Destination>C:\\fixture\\target</Destination><Verify>" + x.Verify.ToString().ToLowerInvariant() + "</Verify><CompareOnly>" + x.Compare.ToString().ToLowerInvariant() + "</CompareOnly><Completed><ItemResult><Source>C:\\fixture\\source\\a</Source><Target>C:\\fixture\\target\\a</Target><Status>Matched</Status><Hash>savedhash</Hash></ItemResult></Completed></JobRecord>");
                var j = Jobs.Load(file); Require(Jobs.GetMode(j) == x.Mode && Jobs.Hashes(j).Count == 1, "Legacy job mapping/checkpoint lost");
                Jobs.Save(file + ".roundtrip", j); Require(Jobs.GetMode(Jobs.Load(file + ".roundtrip")) == x.Mode, "Mode round-trip failed");
            }
        });
        Test("Real 0.3.5.4 serializer fixture loads in 0.4.6", () => {
            string fixture = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "legacy354job.xml");
            var job = Jobs.Load(fixture);
            Require(job.Build == "0.3.5.4" && Jobs.GetMode(job) == OperationMode.VerifiedCopy && Jobs.Hashes(job).Count == 1, "Original serializer fixture incompatible");
        });
        Test("CSV retains legacy columns and adds explicit verification states", () => {
            string file = Path.Combine(root, "report.csv"); Jobs.Export(file, new[] { new ItemResult { Status="Skipped", Source="s", Target="t", Reason="size/time policy" } });
            var lines = File.ReadAllLines(file); Require(lines[0].StartsWith("Status,Source,Destination,Bytes,Reason,SHA256,PreviousVersion,") && lines[1].Contains("Not Verified"), "Report compatibility/state lost");
        });
        Test("Log batching, explicit boundary flush, timer and failures", () => {
            var target = new CountingWriter(); var log = new SafeLogFile(target);
            log.WriteLine("normal 1"); Require(target.Flushes == 0, "Every line still flushes");
            log.Flush(); Require(target.Flushes == 1, "Explicit flush failed");
            log.WriteLine("pending timer"); Require(SpinWait.SpinUntil(() => target.Flushes >= 2, 5000), "One-second log timer did not flush");
            log.WriteLine("ERROR boundary"); Require(target.Flushes >= 3, "Failure boundary not flushed");
            target.ThrowOnFlush = true; log.WriteLine("pending"); log.Flush(); Require(log.Failed, "Flush failure hidden"); log.Dispose();
        });
        Test("Scratch array reused across copy and hash operations", () => {
            var f = new Fixture(); f.Put(false, "a", "a"); f.Put(false, "b", "b"); f.Run(OperationMode.VerifiedCopy);
            var field = typeof(CopyEngine).GetField("scratch", BindingFlags.Instance | BindingFlags.NonPublic); object buffer = field.GetValue(f.Engine);
            f.Run(OperationMode.Compare); Require(buffer != null && ReferenceEquals(buffer, field.GetValue(f.Engine)), "Scratch array replaced");
        });
        Test("UI exposes three modes and old job mode restoration", () => {
            new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var window = new MainWindow(new[] { "--test-no-integration" }); var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var selector = (ComboBox)typeof(MainWindow).GetField("mode", flags).GetValue(window);
            var ask = (CheckBox)typeof(MainWindow).GetField("askNewer", flags).GetValue(window);
            Require(selector.Items.Count == 3 && selector.SelectedIndex == 1 && ask.IsChecked == false, "Modes/default safety not exposed");
            window.Close();
        });
        Test("UI Fast Copy completion is explicitly not verified", () => {
            var f = new Fixture(); f.Put(false, "a", "copy"); var w = TestWindow(f);
            ((ComboBox)Field(w, "mode")).SelectedIndex = (int)OperationMode.FastCopy;
            Invoke(w, "Start", false); var job = WaitJob(w);
            Require(Jobs.GetMode(job) == OperationMode.FastCopy && job.Completed.Any(x => x.Status == "Copied") && !job.Completed.Any(x => x.Status == "Matched"), "UI ignored Fast Copy");
            Require(((TextBlock)Field(w, "status")).Text.Contains("not verified"), "UI claims verification"); w.Close();
        });
        Test("UI Repair All protects newer files and retains unresolved rows", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA"); f.Put(true, "a", "BBBB", New); var w = TestWindow(f);
            Invoke(w, "Start", true); WaitJob(w); Invoke(w, "SyncDifferences"); var job = WaitJob(w);
            Require(job.Completed.Any(x => x.Status == "Protected newer") && job.State != "Completed" && f.Read("a") == "BBBB", "UI Repair All overrode protection or hid unresolved state"); w.Close();
        });
        Test("UI selected repair always verifies and retains original whole scope", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA"); f.Put(false, "b", "BBBB"); f.Put(true, "b", "BBBB"); var w = TestWindow(f);
            Invoke(w, "Start", true); var compare = WaitJob(w);
            var row = compare.Completed.Single(x => !x.Directory && x.Status == "Missing");
            ((DataGrid)Field(w, "resultGrid")).SelectedItems.Add(row);
            ((ComboBox)Field(w, "mode")).SelectedIndex = (int)OperationMode.FastCopy;
            Invoke(w, "RepairSelected"); var repair = WaitJob(w);
            Require(repair.Completed.Any(x => x.Status == "Matched") && repair.Verify && repair.RepairOnly && repair.Selection.Count == 1, "Selected repair not verified/subset lost");
            string savedRepair = (string)Field(w, "currentJobFile");
            Invoke(w, "LoadDefinition", savedRepair);
            ((ComboBox)Field(w, "mode")).SelectedIndex = (int)OperationMode.Compare;
            Invoke(w, "Start", false); var readOnly = WaitJob(w);
            Require(readOnly.CompareOnly && !readOnly.RepairOnly && !readOnly.Completed.Any(x => x.Status == "Copied"), "Choosing Compare on a saved repair caused writes");
            Invoke(w, "LoadDefinition", savedRepair); Invoke(w, "Start", false); var resumed = WaitJob(w);
            Require(resumed.RepairOnly && resumed.Completed.Any(x => x.Status == "Matched") && !resumed.Completed.Any(x => x.Status == "Copied"), "Resumed repair became unconditional Replace");
            f.Put(false, "late", "new scope entry"); Invoke(w, "CompareWholeScope"); var full = WaitJob(w);
            Require(full.Selection.Count == 0 && full.Completed.Any(x => x.Status == "Missing" && x.Source.EndsWith("late")), "Whole-scope compare stayed restricted to repair subset"); w.Close();
        });
        // 0.3.7 fix 1: per-run ancestor cache for link/junction checks.
        Test("0.3.7 path cache: skipped files on a deep tree need far fewer attribute reads", () => {
            var f = new Fixture(); string deep = Path.Combine("l1", "l2", "l3", "l4", "l5", "l6", "l7", "l8");
            for (int i = 0; i < 40; i++) f.Put(false, Path.Combine(deep, "f" + i + ".txt"), "content " + i);
            var r = f.Run(OperationMode.VerifiedCopy);
            Require(r.Copied == 40 && r.Matched == 40 && r.Errors == 0, "Deep copy did not complete cleanly");
            double copyPerFile = (double)f.Engine.PathAttributeReads / 40;
            f.Engine.PathAttributeReads = 0; var skip = f.Run(OperationMode.FastCopy);
            Require(skip.Skipped == 40 && skip.Copied == 0, "Second run did not skip");
            int depth = Path.Combine(f.Target, deep, "f0.txt").Split('\\').Length;
            double skipPerFile = (double)f.Engine.PathAttributeReads / 40;
            Console.WriteLine("INFO path attribute reads per file: copy=" + copyPerFile.ToString("F1") + " skip=" + skipPerFile.ToString("F1") + " (path depth " + depth + ")");
            Require(skipPerFile < depth, "Ancestor checks were not cached for skips: " + skipPerFile + " reads per file at depth " + depth);
        });
        Test("0.3.7 path cache: junction at a deep destination ancestor is still blocked", () => {
            var f = new Fixture(); string elsewhere = Path.Combine(f.Dir, "elsewhere"); Directory.CreateDirectory(elsewhere);
            Directory.CreateDirectory(Path.Combine(f.Target, "x")); Junction(Path.Combine(f.Target, "x", "y"), elsewhere);
            f.Put(false, Path.Combine("x", "y", "z", "file.txt"), "must not land elsewhere");
            var r = f.Run(OperationMode.VerifiedCopy);
            Require(r.Errors > 0 && r.Copied == 0, "Copy through a destination junction was not blocked");
            Require(Directory.GetFileSystemEntries(elsewhere, "*", SearchOption.AllDirectories).Length == 0, "Data was written through the junction");
        });
        Test("0.3.7 path cache: parent folder swapped for a junction mid-run is caught", () => {
            var f = new Fixture(); string elsewhere = Path.Combine(f.Dir, "elsewhere"); Directory.CreateDirectory(elsewhere);
            f.Put(false, Path.Combine("d", "f1.txt"), "first"); f.Put(false, Path.Combine("d", "f2.txt"), "second");
            string targetDir = Path.Combine(f.Target, "d"); bool swapped = false;
            f.Engine.BeforeCopy = source => {
                if (!swapped && source.EndsWith("f2.txt")) { swapped = true; Directory.Move(targetDir, targetDir + "-moved"); Junction(targetDir, elsewhere); }
            };
            var r = f.Run(OperationMode.VerifiedCopy);
            Require(swapped, "Swap hook did not run");
            Require(r.Copied == 1 && r.Errors > 0, "Mid-run parent junction was not detected");
            Require(Directory.GetFileSystemEntries(elsewhere).Length == 0, "Data was written through the swapped-in junction");
        });
        Test("0.3.7 path cache: grandparent swapped for a junction mid-run is caught before writing", () => {
            var f = new Fixture(); string elsewhere = Path.Combine(f.Dir, "elsewhere"); Directory.CreateDirectory(Path.Combine(elsewhere, "sub"));
            f.Put(false, Path.Combine("g", "sub", "f1.txt"), "first"); f.Put(false, Path.Combine("g", "sub", "f2.txt"), "second");
            string targetDir = Path.Combine(f.Target, "g"); bool swapped = false;
            f.Engine.BeforeCopy = source => {
                if (!swapped && source.EndsWith("f2.txt")) { swapped = true; Directory.Move(targetDir, targetDir + "-moved"); Junction(targetDir, elsewhere); }
            };
            var r = f.Run(OperationMode.VerifiedCopy);
            Require(swapped, "Swap hook did not run");
            Require(r.Copied == 1 && r.Errors > 0, "Mid-run grandparent junction was not detected");
            Require(Directory.GetFileSystemEntries(Path.Combine(elsewhere, "sub")).Length == 0, "Data was written through the swapped-in junction");
        });
        Test("0.3.7 path cache: cache is cleared between runs", () => {
            var f = new Fixture(); f.Put(false, Path.Combine("a", "b", "c", "f.txt"), "one");
            f.Run(OperationMode.FastCopy);
            File.SetLastWriteTimeUtc(f.Put(false, Path.Combine("a", "b", "c", "f.txt"), "two"), New);
            f.Engine.PathAttributeReads = 0; f.Run(OperationMode.FastCopy, ExistingRule.Replace);
            Require(f.Engine.PathAttributeReads >= Path.Combine(f.Target, "a", "b", "c", "f.txt").Split('\\').Length, "Second run reused the previous run's cache");
            Require(f.Read(Path.Combine("a", "b", "c", "f.txt")) == "two", "Second run did not copy");
        });
        // 0.3.7: Explorer Ctrl+V launches count down before starting.
        Test("0.3.7 paste countdown starts the job when left alone", () => {
            var f = new Fixture(); f.Put(false, "a", "countdown"); var w = TestWindow(f);
            typeof(MainWindow).GetField("autoStartSeconds", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(w, 1);
            Invoke(w, "BeginAutoStart", false);
            Require(((TextBlock)Field(w, "phaseText")).Text.StartsWith("Starting in"), "Countdown not shown");
            Require(Field(w, "currentJob") == null, "Job started before the countdown ended");
            var job = WaitJob(w);
            Require(job.Completed.Any(x => x.Status == "Copied" || x.Status == "Matched") && f.Read("a") == "countdown", "Countdown did not start the job"); w.Close();
        });
        Test("0.3.7 paste countdown: any click stops it and nothing starts", () => {
            var f = new Fixture(); f.Put(false, "a", "countdown"); var w = TestWindow(f);
            typeof(MainWindow).GetField("autoStartSeconds", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(w, 1);
            Invoke(w, "BeginAutoStart", false);
            w.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
            var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            timer.Tick += (_, __) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
            Require(Field(w, "currentJob") == null && !(bool)Field(w, "busy") && !File.Exists(Path.Combine(f.Target, "a")), "Job started after the countdown was interrupted");
            Require(((TextBlock)Field(w, "status")).Text.Contains("Countdown stopped"), "Interruption not explained");
            Invoke(w, "Start", false); var job = WaitJob(w);
            Require(f.Read("a") == "countdown", "Start button did not run the job after the countdown was stopped"); w.Close();
        });
        // 0.3.9 interface changes.
        Test("0.3.9 slim header: version in title bar, theme is a lightbulb icon", () => {
            var f = new Fixture(); var w = TestWindow(f);
            Require(w.Title.Contains("0.4.6"), "Version missing from window title"); Require(((TextBlock)Field(w, "percentText")).Text == "", "Idle percentage placeholder still shown");
            var theme = (Button)Field(w, "themeButton");
            Require(theme.Content is TextBlock t && t.Text == "\ue82f", "Theme button is not the lightbulb icon");
            Require(((FrameworkElement)Field(w, "integrationStatus")).Parent is StackPanel, "Explorer status line lost");
            Require(((Button)Field(w, "detailsButton")).Parent is DockPanel row && row.Children.OfType<WrapPanel>().Any(), "Compact-view button is not on the bottom button row");
            w.Close();
        });
        Test("0.3.9 count links open Results filtered to that status", () => {
            var f = new Fixture(); f.Put(false, "new.txt", "copy me"); f.Put(false, "same.txt", "same"); f.Put(true, "same.txt", "same");
            var w = TestWindow(f); ((ComboBox)Field(w, "mode")).SelectedIndex = (int)OperationMode.FastCopy;
            Invoke(w, "Start", false); WaitJob(w);
            var links = (System.Collections.Generic.Dictionary<string, Button>)Field(w, "countButtons");
            Require(((string)links["Skipped"].Content) == "Skipped 1" && ((string)links["Copied"].Content) == "Copied 1", "Counts not shown as links: " + links["Skipped"].Content + " / " + links["Copied"].Content);
            links["Skipped"].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(Field(w, "results")).Cast<ItemResult>().ToList();
            Require(view.Count == 1 && view[0].Status == "Skipped" && view[0].Target.EndsWith("same.txt"), "Skipped link did not filter Results");
            var tabs = (TabControl)Field(w, "pages"); Require(tabs.SelectedIndex == tabs.Items.Count - 1, "Results tab not opened");
            Require(((TextBlock)Field(w, "statusFilterText")).Text.Contains("skipped"), "Filter not explained");
            Invoke(w, "ClearStatusFilter");
            var all = System.Windows.Data.CollectionViewSource.GetDefaultView(Field(w, "results")).Cast<ItemResult>().ToList();
            Require(all.Any(x => x.Status == "Copied") && all.Any(x => x.Status == "Skipped"), "Show all did not clear the filter");
            w.Close();
        });
        Test("0.3.9 compact view hides the details area and restores it", () => {
            var f = new Fixture(); var w = TestWindow(f); var tabs = (TabControl)Field(w, "pages");
            Invoke(w, "SetCompact", true, false);
            Require(tabs.Visibility == Visibility.Collapsed && (bool)Field(w, "compactView"), "Details not hidden");
            Require(((Grid)Field(w, "rootGrid")).Margin.Left == 8.0 && ((Border)Field(w, "jobCardBorder")).Margin.Bottom == 0.0, "Compact view kept the extra spacing");
            Invoke(w, "SetCompact", false, false);
            Require(tabs.Visibility == Visibility.Visible && !(bool)Field(w, "compactView"), "Details not restored");
            Require(((Grid)Field(w, "rootGrid")).Margin.Left == 12.0 && ((Border)Field(w, "jobCardBorder")).Margin.Bottom == 12.0, "Full view spacing not restored");
            w.Close();
        });
        // 0.3.9.2: cancelling a large job must not flood the window.
        Test("0.3.9.2 cancel caps not-reached rows and writes the full list to a file", () => {
            var f = new Fixture(); for (int i = 0; i < 1500; i++) f.Put(false, "f" + i.ToString("D4") + ".txt", "x" + i);
            var plan = f.Plan(); var cts = new CancellationTokenSource(); var rows = new List<ItemResult>(); var logLines = new List<string>();
            string list = Path.Combine(f.Dir, "not-reached.txt");
            f.Engine.NotReachedListPath = list; f.Engine.ItemCompleted = rows.Add; f.Engine.Log = logLines.Add;
            f.Engine.Progress = u => { if (u.Phase == "Copying" && u.FileBytes > 0) cts.Cancel(); };
            bool cancelled = false; try { f.Engine.Run(plan, ExistingRule.Replace, OperationMode.FastCopy, cts.Token); } catch (OperationCanceledException) { cancelled = true; }
            int notReachedRows = rows.Count(x => x.Status == "Not reached");
            int listed = File.ReadAllLines(list).Length - 1;
            Console.WriteLine("INFO not-reached rows=" + notReachedRows + " listed in file=" + listed);
            Require(cancelled && notReachedRows == 1000, "Not-reached rows not capped: " + notReachedRows);
            Require(listed > 1000 && listed < 1500, "Full not-reached list not written: " + listed);
            Require(logLines.Any(l => l.StartsWith("NOT REACHED ") && l.Contains("full list")), "Summary log line missing");
        });
        Test("0.3.9.2 activity log shows only the last 2,000 lines of a burst", () => {
            var f = new Fixture(); var w = TestWindow(f);
            var append = typeof(MainWindow).GetMethod("Append", BindingFlags.Instance | BindingFlags.NonPublic);
            for (int i = 0; i < 10000; i++) append.Invoke(w, new object[] { "NOT REACHED line " + i });
            Invoke(w, "FlushLog");
            string text = ((TextBox)Field(w, "log")).Text;
            int lines = text.Split('\n').Length;
            Require(text.Contains("earlier lines not shown") && text.Contains("line 9999") && !text.Contains("line 100\r") && lines <= 2005, "Log burst not trimmed: " + lines + " lines");
            w.Close();
        });
        Test("0.3.9.2 large result batches are added to the grid quickly", () => {
            var f = new Fixture(); var w = TestWindow(f);
            var queue = (Queue<ItemResult>)Field(w, "rowQueue");
            for (int i = 0; i < 100000; i++) queue.Enqueue(new ItemResult { Source = "s" + i, Target = "t" + i, Status = "Not reached", Reason = "Cancelled before this file was reached" });
            var sw = System.Diagnostics.Stopwatch.StartNew(); Invoke(w, "DrainResults"); sw.Stop();
            Console.WriteLine("INFO 100,000 result rows drained in " + sw.ElapsedMilliseconds + " ms");
            Require(((System.Collections.ICollection)Field(w, "results")).Count == 100000 && sw.ElapsedMilliseconds < 20000, "Bulk drain too slow: " + sw.ElapsedMilliseconds + " ms");
            w.Close();
        });
        Test("0.3.9.2 log retention keeps the newest 30 log sets and old paste requests expire", () => {
            var f = new Fixture(); string logs = Path.Combine(f.Dir, "Logs"), paste = Path.Combine(f.Dir, "PasteRequests"); Directory.CreateDirectory(logs); Directory.CreateDirectory(paste);
            var now = DateTime.UtcNow;
            Action<string, DateTime> make = (path, time) => { File.WriteAllText(path, "x"); File.SetLastWriteTimeUtc(path, time); };
            for (int i = 0; i < 40; i++)
            {
                string log = Path.Combine(logs, "job" + i.ToString("D2") + ".log"); var t = now.AddDays(-(40 - i));   // job39 newest
                make(log, t); make(log + ".results.csv", t); make(log + ".summary.txt", t);
            }
            // An old log whose report was touched a moment ago (job still finishing) must be left alone.
            File.SetLastWriteTimeUtc(Path.Combine(logs, "job00.log.summary.txt"), now);
            for (int i = 0; i < 3; i++) make(Path.Combine(paste, "old" + i + ".txt"), now.AddDays(-10));
            for (int i = 0; i < 2; i++) make(Path.Combine(paste, "new" + i + ".txt"), now.AddDays(-1));
            string summary = Housekeeping.ApplyRetention(logs, paste, 30, 7, now);
            Console.WriteLine("INFO " + summary);
            var remaining = Directory.GetFiles(logs, "*.log").Select(Path.GetFileName).ToList();
            Require(remaining.Count == 31 && remaining.Contains("job39.log") && remaining.Contains("job10.log") && !remaining.Contains("job09.log") && remaining.Contains("job00.log"), "Wrong logs kept: " + remaining.Count);
            Require(!File.Exists(Path.Combine(logs, "job05.log.results.csv")) && !File.Exists(Path.Combine(logs, "job05.log.summary.txt")), "Companion reports of a removed log were left behind");
            Require(File.Exists(Path.Combine(logs, "job00.log.results.csv")), "A recently touched log set was partly removed");
            Require(Directory.GetFiles(paste).Length == 2 && Directory.GetFiles(paste, "new*").Length == 2, "Paste-request expiry wrong");
            Require(summary != null && summary.Contains("9 old log set"), "Summary missing or wrong: " + summary);
            Require(Housekeeping.ApplyRetention(logs, paste, 0, 7, now) == null && Directory.GetFiles(logs, "*.log").Length == 31, "keep=0 must disable retention");
        });
        // 0.3.10: network outages and unexpected errors.
        Func<IOException> netError = () => new IOException("The specified network name is no longer available.", unchecked((int)0x80070040));
        Test("0.3.10 a short network outage is waited out and the copy completes", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA"); f.Put(false, "b", "BBBB");
            f.Engine.OutageFirstWaitMs = 20; f.Engine.OutageMaxWaitMs = 50; int failures = 3; var logs = new List<string>(); var notes = new List<string>();
            f.Engine.Log = logs.Add; f.Engine.Progress = u => { if (u.Phase == "Waiting for network") notes.Add(u.Note); };
            f.Engine.BeforeCopy = _ => { if (failures-- > 0) throw netError(); };
            var r = f.Run(OperationMode.VerifiedCopy);
            Require(r.Copied == 2 && r.Matched == 2 && r.Errors == 0 && f.Read("a") == "AAAA" && f.Read("b") == "BBBB", "Copy did not complete after the outage");
            Require(logs.Count(l => l.StartsWith("NETWORK WAIT ")) == 3 && notes.Count == 3 && notes[0].Contains("Retrying in"), "Network waits not logged/shown");
        });
        Test("0.3.10 an outage longer than the limit stops the job cleanly and it resumes", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA"); f.Put(false, "b", "BBBB"); f.Put(false, "c", "CCCC");
            f.Engine.OutageFirstWaitMs = 20; f.Engine.OutageMaxWaitMs = 50; f.Engine.OutageWaitLimit = TimeSpan.FromMilliseconds(300); var rows = new List<ItemResult>();
            f.Engine.ItemCompleted = rows.Add; f.Engine.BeforeCopy = src => { if (src.EndsWith("b")) throw netError(); };
            Exception stopped = null; try { f.Engine.Run(f.Plan(), ExistingRule.Replace, OperationMode.FastCopy, CancellationToken.None); } catch (Exception ex) { stopped = ex; }
            Require(stopped is NetworkOutageException && stopped.Message.Contains("resume"), "Job did not stop with a network-outage reason: " + stopped);
            Require(!File.Exists(Path.Combine(f.Target, "b")) && !Directory.GetFiles(f.Target, "*.partial").Any(), "Unexpected destination state after the outage stop");
            Require(rows.Any(x => x.Status == "Unchecked" && x.Source.EndsWith("b")), "Interrupted file not reported");
            foreach (string other in new[] { "a", "c" })
                Require(File.Exists(Path.Combine(f.Target, other)) || rows.Any(x => x.Status == "Not reached" && x.Source.EndsWith(other)), "File " + other + " neither copied nor reported as not reached");
            f.Engine.BeforeCopy = _ => { }; f.Engine.ItemCompleted = _ => { };
            var resumed = f.Run(OperationMode.VerifiedCopy);
            Require(resumed.Clean && f.Read("b") == "BBBB" && f.Read("c") == "CCCC", "Resume after the outage failed");
        });
        Test("0.3.10 Cancel during a network wait takes effect immediately", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA");
            f.Engine.OutageFirstWaitMs = 30000; f.Engine.BeforeCopy = _ => { throw netError(); };
            var cts = new CancellationTokenSource(); cts.CancelAfter(300); var sw = System.Diagnostics.Stopwatch.StartNew(); Exception caught = null;
            try { f.Engine.Run(f.Plan(), ExistingRule.Replace, OperationMode.FastCopy, cts.Token); } catch (Exception ex) { caught = ex; }
            sw.Stop(); Console.WriteLine("INFO cancel during network wait took " + sw.ElapsedMilliseconds + " ms");
            Require(caught is OperationCanceledException && !(caught is NetworkOutageException) && sw.ElapsedMilliseconds < 5000, "Cancel during a network wait was not prompt: " + sw.ElapsedMilliseconds + " ms");
        });
        Test("0.3.10 ordinary I/O errors keep the quick-retry behaviour", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA"); var logs = new List<string>(); f.Engine.Log = logs.Add;
            f.Engine.BeforeCopy = _ => { throw new IOException("Simulated disk full"); };
            var sw = System.Diagnostics.Stopwatch.StartNew(); var r = f.Run(OperationMode.FastCopy); sw.Stop();
            Require(r.Errors == 1 && r.Copied == 0 && !logs.Any(l => l.StartsWith("NETWORK ")) && sw.ElapsedMilliseconds < 5000, "Non-network error was treated as an outage");
            Require(!NetworkOutageException.IsNetworkError(new FileNotFoundException("x")) && NetworkOutageException.IsNetworkError(netError()), "Network error classification wrong");
        });
        Test("0.3.10 crash handler writes a report and saves the running job as Stopped", () => {
            var f = new Fixture(); var w = TestWindow(f); var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            string jobFile = Path.Combine(f.Dir, "crash-job.clearcopy.xml");
            var job = new JobRecord { Sources = new[] { f.Source }, Destination = f.Target, State = "Running" };
            job.Completed.Add(new ItemResult { Source = "s", Target = "t", Status = "Copied" });
            typeof(MainWindow).GetField("currentJob", flags).SetValue(w, job);
            typeof(MainWindow).GetField("currentJobFile", flags).SetValue(w, jobFile);
            typeof(MainWindow).GetField("busy", flags).SetValue(w, true);
            CrashHandler.Interactive = false; CrashHandler.LogDirectory = Path.Combine(f.Dir, "crash-logs");
            string report = CrashHandler.Handle(new InvalidOperationException("Simulated unexpected failure"), "test", false);
            typeof(MainWindow).GetField("busy", flags).SetValue(w, false);
            Require(report != null && File.ReadAllText(report).Contains("Simulated unexpected failure") && File.ReadAllText(report).Contains("Version:"), "Crash report not written");
            var saved = Jobs.Load(jobFile);
            Require(saved.State == "Stopped" && saved.Warning.Contains("Simulated unexpected failure") && saved.Completed.Count == 1, "Running job not saved as resumable");
            w.Close();
        });
        // 0.3.10.1: activity log display and message clean-up.
        Test("0.3.10.1 activity log shows paths after the destination root; the log file keeps full paths", () => {
            var f = new Fixture(); var w = TestWindow(f); string root = @"\\nas\Data\dataam";
            Require(MainWindow.RelativeToRoot(@"COPIED \\NAS\data\dataam\A\b.pdf | previous version: \\nas\Data\dataam\A\x.bak", root + "\\") == @"COPIED A\b.pdf | previous version: A\x.bak", "Relative path conversion wrong");
            Require(MainWindow.RelativeToRoot("FOLDER CREATED C:\\other\\x", root + "\\") == "FOLDER CREATED C:\\other\\x", "Unrelated path changed");
            var log = SafeLogFile.Open(Path.Combine(f.Dir, "display.log"));
            typeof(MainWindow).GetField("writer", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(w, log);
            Invoke(w, "SetDisplayRoot", root);
            var append = typeof(MainWindow).GetMethod("Append", BindingFlags.Instance | BindingFlags.NonPublic);
            append.Invoke(w, new object[] { @"COPIED \\nas\Data\dataam\AJM\file.pdf : Primary stream copied" });
            Invoke(w, "FlushLog"); log.Dispose();
            typeof(MainWindow).GetField("writer", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(w, null);
            string shown = ((TextBox)Field(w, "log")).Text;
            Require(shown.Contains(@"COPIED AJM\file.pdf") && !shown.Contains(@"\\nas\Data\dataam\AJM"), "Activity log not relative: " + shown);
            Require(File.ReadAllText(Path.Combine(f.Dir, "display.log")).Contains(@"COPIED \\nas\Data\dataam\AJM\file.pdf"), "Log file lost the full path");
            Require(((TextBlock)Field(w, "logRootLabel")).Text.Contains(@"\\nas\Data\dataam\") && ((TextBlock)Field(w, "logRootLabel")).Visibility == Visibility.Visible, "Root label not shown");
            Require(Application.Current.Resources[SystemColors.ControlBrushKey] == Application.Current.Resources["Field"], "Scrollbar corner not themed");
            w.Close();
        });
        Test("0.3.10.1 network messages have no stray line breaks", () => {
            Require(NetworkOutageException.Clean("The network path was not found.\r\n") == "The network path was not found", "Message not cleaned");
            var ex = new NetworkOutageException(@"\\nas\x", TimeSpan.FromMinutes(10), new IOException("The network path was not found.\r\n", unchecked((int)0x80070035)));
            Require(!ex.Message.Contains("\n") && ex.Message.Contains("10 min"), "Outage message: " + ex.Message);
        });
        // 0.4.0: large jobs.
        Test("0.4.0 engine keeps every problem row but only the first N ordinary rows", () => {
            var f = new Fixture(); for (int i = 0; i < 40; i++) f.Put(false, "f" + i, "c" + i);
            f.Engine.KeepNormalRows = 5; int reported = 0; f.Engine.ItemCompleted = r => reported++;
            bool gone = false; f.Engine.BeforeCopy = s => { if (!gone && s.EndsWith("f17")) { gone = true; File.Delete(s); } };
            var r = f.Run(OperationMode.FastCopy);
            int issues = r.Items.Count(Jobs.IsIssue);
            Require(gone && r.Copied == 39 && issues >= 1, "copied=" + r.Copied + " issues=" + issues);
            Require(r.Items.Count - issues == 5, "Ordinary rows kept: " + (r.Items.Count - issues));
            Require(reported >= 40, "Not every row was reported for the results list: " + reported);
        });
        Test("0.4.0 final source recheck finds added, changed and removed items without a second scan", () => {
            var f = new Fixture(); for (int i = 0; i < 12; i++) f.Put(false, Path.Combine("d" + (i % 3), "f" + i + ".txt"), "x" + i);
            var rows = new List<ItemResult>(); f.Engine.ItemCompleted = r => rows.Add(r); f.Engine.AuditAfter = true;
            bool hit = false;
            f.Engine.BeforeCopy = s => { if (!hit && s.EndsWith("f5.txt")) { hit = true;
                f.Put(false, Path.Combine("d1", "new.txt"), "new"); f.Put(false, Path.Combine("newdir", "n.txt"), "n");
                File.Delete(Path.Combine(f.Source, "d2", "f8.txt")); File.WriteAllText(Path.Combine(f.Source, "d0", "f9.txt"), "changed content"); } };
            var r = f.Run(OperationMode.VerifiedCopy);
            Func<string, string, bool> has = (end, reason) => rows.Any(x => x.Status == "Changed" && x.Source.EndsWith(end) && x.Reason.StartsWith(reason));
            Require(hit, "Hook did not run");
            Require(has("new.txt", "Added after initial scan") && has("newdir", "Added after initial scan") && has("n.txt", "Added after initial scan"), "Added items not reported");
            Require(has("f9.txt", "Source changed since initial scan"), "Changed file not reported");
            Require(has("f8.txt", "Source removed after initial scan") && rows.Count(x => x.Reason.StartsWith("Source removed")) == 1, "Removed file not reported once");
            Require(rows.Any(x => x.Target == Path.Combine(f.Target, "d1", "new.txt")), "Added item has the wrong destination");
            Require(!r.Clean, "Changed source reported as clean");
            var clean = new Fixture(); for (int i = 0; i < 12; i++) clean.Put(false, Path.Combine("a", "b" + (i % 4), "f" + i), "y" + i);
            clean.Engine.AuditAfter = true; var c = clean.Run(OperationMode.VerifiedCopy);
            Require(c.Clean && c.Changed == 0 && c.Unchecked == 0 && c.Matched == 12, "Unchanged tree flagged: changed=" + c.Changed + " unchecked=" + c.Unchecked);
        });
        Test("0.4.0 UI streams every row to results.csv and keeps the first N in the window", () => {
            var f = new Fixture(); for (int i = 0; i < 25; i++) f.Put(false, Path.Combine("sub", "f" + i), "c" + i);
            var w = TestWindow(f); var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(MainWindow).GetField("resultRowLimit", flags).SetValue(w, 6);
            ((ComboBox)Field(w, "mode")).SelectedIndex = (int)OperationMode.VerifiedCopy;
            Invoke(w, "Start", false); var job = WaitJob(w);
            string list = (string)Field(w, "logPath") + ".results.csv";
            string[] lines = File.ReadAllLines(list);
            Require(lines[0] == Jobs.CsvHeader, "results.csv header");
            Require(lines.Count(l => l.StartsWith("\"Copied\",")) >= 25 && lines.Count(l => l.StartsWith("\"Matched\",")) >= 25, "results.csv does not hold every row: " + lines.Length);
            Require(job.Completed.Count(x => !Jobs.IsIssue(x)) == 6, "Job kept " + job.Completed.Count + " rows");
            Require(((System.Collections.ICollection)Field(w, "results")).Count == job.Completed.Count, "Window rows differ from kept rows");
            string jobFile = (string)Field(w, "currentJobFile");
            Require(jobFile == null || Jobs.Load(jobFile).Completed.Count == job.Completed.Count, "Job file holds a different row count");
            Invoke(w, "ShowResultsFor", "Copied");
            Require(((TextBlock)Field(w, "statusFilterText")).Text.Contains("first"), "Capped list not explained: " + ((TextBlock)Field(w, "statusFilterText")).Text);
            Require(((Button)Field(w, "openFullList")).Visibility == Visibility.Visible, "Open full list button hidden");
            Require(File.ReadAllText((string)Field(w, "logPath") + ".summary.txt").Contains(".results.csv"), "Summary does not point at the full list");
            w.Close();
        });
        Test("0.4.0 small job does not show the capped-list button", () => {
            var f = new Fixture(); f.Put(false, "a", "one"); var w = TestWindow(f);
            Invoke(w, "Start", false); WaitJob(w); Invoke(w, "ShowResultsFor", "Copied");
            Require(((Button)Field(w, "openFullList")).Visibility != Visibility.Visible && !((TextBlock)Field(w, "statusFilterText")).Text.Contains("first"), "Uncapped list shown as capped");
            w.Close();
        });
        Test("0.4.0 sync restarts the full list before its final comparison", () => {
            var f = new Fixture(); for (int i = 0; i < 8; i++) f.Put(false, "m" + i, "missing " + i);
            var w = TestWindow(f); typeof(MainWindow).GetField("resultRowLimit", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(w, 2);
            Invoke(w, "Start", true); WaitJob(w); Invoke(w, "SyncDifferences"); var job = WaitJob(w);
            string[] lines = File.ReadAllLines((string)Field(w, "logPath") + ".results.csv");
            Require(lines.Count(l => l.StartsWith("\"Matched\",")) >= 8 && !lines.Any(l => l.StartsWith("\"Copied\",") || l.StartsWith("\"Missing\",")), "Final list mixes repair rows: " + string.Join(" | ", lines.Take(6)));
            for (int i = 0; i < 8; i++) Require(f.Read("m" + i) == "missing " + i, "Not repaired: m" + i);
            w.Close();
        });
        // 0.4.1: Fix access.
        Test("0.4.1 access helpers: top-most paths, missing paths are not 'denied', retried folders are rescanned", () => {
            var top = AccessFix.TopMost(new[] { @"G:\a\b", @"G:\a", @"G:\ab", @"G:\a\b\c.txt", @"G:\x" });
            Require(top.Count == 3 && top.Contains(@"G:\a") && top.Contains(@"G:\ab") && top.Contains(@"G:\x"), "TopMost: " + string.Join(";", top));
            var f = new Fixture(); Require(!AccessFix.Denied(Path.Combine(f.Source, "nothing-here")), "Missing path reported as denied");
            f.Put(false, Path.Combine("d", "one.txt"), "1"); f.Put(false, Path.Combine("d", "e", "two.txt"), "2");
            var plan = new List<Entry> { new Entry { Source = Path.Combine(f.Source, "d"), Target = Path.Combine(f.Target, "d"), Directory = true } };
            f.Engine.RescanAll(plan, plan.ToList(), CancellationToken.None);
            Require(plan.Count == 4 && plan.Any(e => e.Target == Path.Combine(f.Target, "d", "e", "two.txt")) && plan[0].Source == Path.Combine(f.Source, "d"), "Folder not expanded: " + plan.Count);
        });
        Test("0.4.1 locked folder found during the scan is fixed and copied in the same job", () => {
            var f = new Fixture(); f.Put(false, "open.txt", "open"); f.Put(false, Path.Combine("locked", "inner.txt"), "inner"); f.Put(false, Path.Combine("locked", "deeper", "deep.txt"), "deep");
            string locked = Path.Combine(f.Source, "locked"); LockOut(locked);
            try
            {
                Require(AccessFix.Denied(locked), "Test folder is not locked");
                var w = TestWindow(f); var flags = BindingFlags.Instance | BindingFlags.NonPublic; List<string> asked = null;
                typeof(MainWindow).GetField("accessPromptOverride", flags).SetValue(w, (Func<List<string>, string>)(r => { asked = r; return "fix"; }));
                typeof(MainWindow).GetField("accessFixInProcess", flags).SetValue(w, true);
                ((ComboBox)Field(w, "mode")).SelectedIndex = (int)OperationMode.FastCopy;
                Invoke(w, "Start", false); var job = WaitJob(w);
                Require(asked != null && asked.Count == 1 && asked[0] == locked, "Prompt did not list the locked folder");
                Require(f.Read(Path.Combine("locked", "inner.txt")) == "inner" && f.Read(Path.Combine("locked", "deeper", "deep.txt")) == "deep" && f.Read("open.txt") == "open", "Locked folder not copied after the fix");
                Require(!job.Completed.Any(Jobs.IsIssue), "Issues remain: " + string.Join(" | ", job.Completed.Where(Jobs.IsIssue).Select(x => x.Status + " " + x.Reason)));
                string changes = Path.Combine(Path.GetDirectoryName((string)Field(w, "logPath")), "access-changes.txt");
                Require(File.Exists(changes) && File.ReadAllText(changes).Contains("permissions before: ") && File.ReadAllText(changes).Contains(locked), "Original permissions not recorded");
                Require(((string)Field(w, "logPath") != null) && File.ReadAllText((string)Field(w, "logPath")).Contains("ACCESS FIXED " + locked), "Fix not logged");
                w.Close();
            }
            finally { Unlock(locked); }
        });
        Test("0.4.1 'Skip them' copies the rest, lists the locked folder once and does not ask again", () => {
            var f = new Fixture(); f.Put(false, "open.txt", "open"); f.Put(false, Path.Combine("locked", "inner.txt"), "inner");
            string locked = Path.Combine(f.Source, "locked"); LockOut(locked);
            try
            {
                var w = TestWindow(f); var flags = BindingFlags.Instance | BindingFlags.NonPublic; int asked = 0;
                typeof(MainWindow).GetField("accessPromptOverride", flags).SetValue(w, (Func<List<string>, string>)(r => { asked++; return "skip"; }));
                typeof(MainWindow).GetField("accessFixInProcess", flags).SetValue(w, true);
                ((ComboBox)Field(w, "mode")).SelectedIndex = (int)OperationMode.FastCopy;
                Invoke(w, "Start", false); var job = WaitJob(w); PumpUntil(() => false, 1.5);
                Require(f.Read("open.txt") == "open" && !File.Exists(Path.Combine(f.Target, "locked", "inner.txt")), "Skip did not leave the locked folder alone");
                Require(job.Completed.Any(x => x.Status == "Unchecked" && x.Source == locked), "Locked folder not listed");
                Require(asked == 1 && w.AccessFixRuns == 0, "Asked " + asked + " times, fixes " + w.AccessFixRuns);
                Require(AccessFix.Denied(locked), "Folder changed although skipped");
                w.Close();
            }
            finally { Unlock(locked); }
        });
        Test("0.4.1 'Cancel job' at the access prompt cancels before copying", () => {
            var f = new Fixture(); f.Put(false, "open.txt", "open"); f.Put(false, Path.Combine("locked", "inner.txt"), "inner");
            string locked = Path.Combine(f.Source, "locked"); LockOut(locked);
            try
            {
                var w = TestWindow(f); var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(MainWindow).GetField("accessPromptOverride", flags).SetValue(w, (Func<List<string>, string>)(r => "cancel"));
                ((ComboBox)Field(w, "mode")).SelectedIndex = (int)OperationMode.FastCopy;
                Invoke(w, "Start", false); var job = WaitJob(w);
                Require(job.State == "Cancelled" && !File.Exists(Path.Combine(f.Target, "open.txt")), "Job not cancelled: " + job.State);
                w.Close();
            }
            finally { Unlock(locked); }
        });
        Test("0.4.1 locked file found while copying is fixed and retried after the job", () => {
            var f = new Fixture(); f.Put(false, "open.txt", "open"); string secret = f.Put(false, Path.Combine("docs", "secret.txt"), "secret"); LockOut(secret);
            try
            {
                var w = TestWindow(f); var flags = BindingFlags.Instance | BindingFlags.NonPublic; List<string> asked = null;
                typeof(MainWindow).GetField("accessPromptOverride", flags).SetValue(w, (Func<List<string>, string>)(r => { asked = r; return "fix"; }));
                typeof(MainWindow).GetField("accessFixInProcess", flags).SetValue(w, true);
                ((ComboBox)Field(w, "mode")).SelectedIndex = (int)OperationMode.FastCopy;
                Invoke(w, "Start", false); var first = WaitJob(w);
                bool atCopy = first.Completed.Any(x => x.Status == "Error" && x.Source == secret && x.Reason.StartsWith("Access denied"));
                Console.WriteLine("INFO locked file detected " + (atCopy ? "while copying (end-of-job prompt)" : "during the scan"));
                Require(atCopy || asked != null, "Locked file was neither reported nor offered for fixing");
                PumpUntil(() => w.AccessFixRuns > 0 && !(bool)Field(w, "busy") && !(bool)Field(w, "starting") && File.Exists(Path.Combine(f.Target, "docs", "secret.txt")), 30);
                Require(asked != null && asked.Count == 1 && asked[0] == secret, "End-of-job prompt did not list the file");
                Require(File.Exists(Path.Combine(f.Target, "docs", "secret.txt")) && f.Read(Path.Combine("docs", "secret.txt")) == "secret", "Locked file not retried after the fix");
                var retry = (JobRecord)Field(w, "currentJob");
                Require(retry != first && !retry.Completed.Any(Jobs.IsIssue), "Retry left issues");
                w.Close();
            }
            finally { Unlock(secret); }
        });
        // 0.4.2
        Test("0.4.2 Update button: 0.4.1 and 0.4.1.0 are the same version", () => {
            Require(SelfInstall.SetupButton(true, new Version("0.4.1"), new Version("0.4.1.0"), true) == null, "Same version offered as an update");
            Require(SelfInstall.SetupButton(true, new Version("0.4.1"), new Version("0.4.2.0"), true) == "Update", "Newer version not offered");
            Require(SelfInstall.SetupButton(true, new Version("0.4.2.0"), new Version("0.4.1.0"), true) == null, "Older version offered");
        });
        Test("0.4.2 destination problems are told apart from source problems", () => {
            var f = new Fixture(); string t = f.Put(true, "ro.txt", "x"); f.Put(true, "rw.txt", "y");
            File.SetAttributes(t, FileAttributes.ReadOnly);
            try
            {
                Require((AccessFix.DestinationProblem(t) ?? "").Contains("read-only"), "Read-only destination not reported");
                Require(AccessFix.DestinationProblem(Path.Combine(f.Target, "rw.txt")) == null && AccessFix.DestinationProblem(Path.Combine(f.Target, "none.txt")) == null, "False destination problem");
                Require(!AccessFix.OnNetwork(f.Source) && AccessFix.OnNetwork(@"\\server\share\x"), "Network detection");
            }
            finally { File.SetAttributes(t, FileAttributes.Normal); }
        });
        Test("0.4.2 locked loose files are listed with locked folders in the first prompt", () => {
            var f = new Fixture(); f.Put(false, Path.Combine("locked", "inner.txt"), "inner"); string loose = f.Put(false, Path.Combine("open", "loose.pdf"), "loose"); f.Put(false, Path.Combine("open", "fine.pdf"), "fine");
            string locked = Path.Combine(f.Source, "locked"); LockOut(locked); LockOut(loose);
            try
            {
                var w = TestWindow(f); var flags = BindingFlags.Instance | BindingFlags.NonPublic; var asked = new List<List<string>>();
                typeof(MainWindow).GetField("accessPromptOverride", flags).SetValue(w, (Func<List<string>, string>)(r => { asked.Add(r); return "fix"; }));
                typeof(MainWindow).GetField("accessFixInProcess", flags).SetValue(w, true);
                ((ComboBox)Field(w, "mode")).SelectedIndex = (int)OperationMode.FastCopy;
                Invoke(w, "Start", false); var job = WaitJob(w); PumpUntil(() => false, 1.0);
                Require(asked.Count == 1 && asked[0].Count == 2 && asked[0].Contains(locked) && asked[0].Contains(loose), "First prompt: " + string.Join(" | ", asked.Select(a => string.Join(";", a))));
                Require(f.Read(Path.Combine("locked", "inner.txt")) == "inner" && f.Read(Path.Combine("open", "loose.pdf")) == "loose" && f.Read(Path.Combine("open", "fine.pdf")) == "fine", "Not all copied");
                Require(!job.Completed.Any(Jobs.IsIssue), "Issues remain");
                Require(File.ReadAllText((string)Field(w, "logPath")).Contains("ACCESS FIXED " + loose), "Loose file fix not logged");
                w.Close();
            }
            finally { Unlock(loose); Unlock(locked); }
        });
        Test("0.4.2 Fix access button fixes and retries after 'Not now', and logs which side refused", () => {
            var f = new Fixture(); f.Put(false, "open.txt", "open"); string secret = f.Put(false, Path.Combine("docs", "secret.txt"), "secret"); LockOut(secret);
            try
            {
                var w = TestWindow(f); var flags = BindingFlags.Instance | BindingFlags.NonPublic; int asked = 0;
                typeof(MainWindow).GetField("accessPromptOverride", flags).SetValue(w, (Func<List<string>, string>)(r => ++asked == 1 ? "skip" : "fix"));
                typeof(MainWindow).GetField("accessFixInProcess", flags).SetValue(w, true);
                ((ComboBox)Field(w, "mode")).SelectedIndex = (int)OperationMode.FastCopy;
                Invoke(w, "Start", false); WaitJob(w);
                PumpUntil(() => asked >= 1, 15); PumpUntil(() => false, 0.5);
                Require(asked == 1 && !File.Exists(Path.Combine(f.Target, "docs", "secret.txt")), "End-of-job prompt not shown or not respected");
                string firstLog = (string)Field(w, "logPath");
                Invoke(w, "FixAccessSelected");
                PumpUntil(() => asked >= 2 && !(bool)Field(w, "busy") && !(bool)Field(w, "starting") && File.Exists(Path.Combine(f.Target, "docs", "secret.txt")), 30);
                Require(asked == 2 && f.Read(Path.Combine("docs", "secret.txt")) == "secret", "Fix access button did not fix and retry");
                string kept = File.ReadAllText(firstLog);
                Require(kept.Contains("ACCESS CHECK after job: 1 item(s); source refuses 1") && kept.Contains("ACCESS CHECK (Fix access button): 1 item(s); source refuses 1"), "Access checks not kept in the job log file");
                w.Close();
            }
            finally { Unlock(secret); }
        });
        Test("0.4.2 items inside a fixed folder that do not inherit get the read entry too", () => {
            var f = new Fixture(); string inner = f.Put(false, Path.Combine("locked", "protected.txt"), "p");
            string locked = Path.Combine(f.Source, "locked");
            var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null); var me = WindowsIdentity.GetCurrent().User;
            var fs = new FileSecurity(); fs.SetAccessRuleProtection(true, false); fs.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.Read, AccessControlType.Allow)); File.SetAccessControl(inner, fs);
            LockOut(locked);
            try
            {
                Require(AccessFix.Denied(locked) && !AccessFix.Denied(inner), "Fixture: folder should be locked, protected file readable");
                var outcome = AccessFix.Apply(new[] { locked }, me, Path.Combine(f.Dir, "changes.txt"), CancellationToken.None);
                Require(outcome.Fixed.Count == 1, "Folder not fixed");
                bool granted = File.GetAccessControl(inner).GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(r => r.IdentityReference.Equals(me) && r.AccessControlType == AccessControlType.Allow);
                Require(granted, "Protected file inside the fixed folder did not get the read entry");
            }
            finally { Unlock(inner); Unlock(locked); }
        });
        // 0.4.3: plain-language Results
        Test("0.4.3 Results wording explains each outcome in plain language", () => {
            var skipFast = new ItemResult { Status = "Skipped", Reason = "Policy skip; size/time is not content verification", FastMode = true };
            Require(skipFast.GridStatus == "Existing file — skipped" && skipFast.Explanation.Contains("same size and date") && skipFast.Explanation.Contains("not checked (Fast Copy)"), "Fast skip: " + skipFast.Explanation);
            var verified = new ItemResult { Status = "Matched", Prior = "Skipped", Reason = "SHA-256 primary streams matched while both files were held read-only" };
            Require(verified.GridStatus == "Existing file — verified" && verified.Explanation.Contains("identical"), "Verified skip: " + verified.GridStatus);
            var differs = new ItemResult { Status = "Mismatch", Prior = "Skipped", Reason = "Primary file contents differ" };
            Require(differs.GridStatus == "Existing file DIFFERS" && differs.Explanation.Contains("Repair"), "Mismatch wording");
            var src = new ItemResult { Status = "Error", Source = @"G:\D\a.pdf", Target = @"\\nas\Data\D\a.pdf", Reason = @"Access denied: Access to the path '\\?\G:\D\a.pdf' is denied." };
            var dst = new ItemResult { Status = "Error", Source = @"G:\D\a.pdf", Target = @"\\nas\Data\D\a.pdf", Reason = @"Access denied: Access to the path '\\?\UNC\nas\Data\D\.clearcopy-1.partial' is denied." };
            Require(src.GridStatus == "Access denied" && src.Explanation.Contains("on the source") && dst.Explanation.Contains("on the destination"), "Access side: " + src.Explanation + " / " + dst.Explanation);
            Require(new ItemResult { Status = "Skipped" }.DisplayStatus == "Policy Skipped — Not Verified", "CSV display status changed");
        });
        Test("0.4.3 Verified Copy shows one Results row per file; reports keep both rows", () => {
            var f = new Fixture(); f.Put(false, "same.txt", "same"); f.Put(true, "same.txt", "same"); f.Put(false, "new.txt", "new");
            var w = TestWindow(f); ((ComboBox)Field(w, "mode")).SelectedIndex = (int)OperationMode.VerifiedCopy;
            Invoke(w, "Start", false); var job = WaitJob(w);
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(Field(w, "results")).Cast<ItemResult>().Where(x => !x.Directory).ToList();
            var same = view.Where(x => x.Target.EndsWith("same.txt")).ToList(); var fresh = view.Where(x => x.Target.EndsWith("new.txt")).ToList();
            Require(same.Count == 1 && same[0].GridStatus == "Existing file — verified", "Existing file rows: " + string.Join(" | ", same.Select(x => x.GridStatus)));
            Require(fresh.Count == 1 && fresh[0].GridStatus == "Copied — verified", "New file rows: " + string.Join(" | ", fresh.Select(x => x.GridStatus)));
            Require(job.Completed.Count(x => x.Target.EndsWith("same.txt")) == 2 && job.Completed.Count(x => x.Target.EndsWith("new.txt")) == 2, "Report rows changed");
            string[] csv = File.ReadAllLines((string)Field(w, "logPath") + ".results.csv");
            Require(csv.Any(l => l.StartsWith("\"Skipped\",")) && csv.Count(l => l.StartsWith("\"Matched\",")) == 2, "results.csv rows changed");
            var links = (Dictionary<string, Button>)Field(w, "countButtons");
            links["Skipped"].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var skipped = System.Windows.Data.CollectionViewSource.GetDefaultView(Field(w, "results")).Cast<ItemResult>().ToList();
            Require(skipped.Count == 1 && skipped[0].Target.EndsWith("same.txt") && skipped[0].Status == "Matched", "Skipped link: " + skipped.Count);
            w.Close();
        });
        Test("0.4.3 destination inside the source is explained without a stack trace", () => {
            var f = new Fixture(); f.Put(false, Path.Combine("sub", "a.txt"), "a"); var w = TestWindow(f);
            ((CheckBox)Field(w, "directCompare")).IsChecked = false; ((TextBox)Field(w, "destination")).Text = Path.GetDirectoryName(f.Source);
            Invoke(w, "Start", false); WaitJob(w); Invoke(w, "FlushLog");
            string shown = ((TextBox)Field(w, "log")).Text;
            Require(((TextBlock)Field(w, "status")).Text.Contains("Choose a destination outside the source") && !shown.Contains("   at ClearCopy"), "Overlap message: " + ((TextBlock)Field(w, "status")).Text);
            w.Close();
        });
        // 0.4.4: source and destination read at the same time during verification
        Test("0.4.4 same-drive detection decides when both copies are read together", () => {
            Require(!CopyEngine.SameStorage(@"G:\Diverse\a.pdf", @"\\192.168.15.101\Data\Diverse\a.pdf"), "G: vs NAS treated as same storage");
            Require(CopyEngine.SameStorage(@"C:\a\x", @"c:\b\y") && CopyEngine.SameStorage(@"\\nas\Data\x", @"\\NAS\data\y"), "Same drive/share not detected");
            Require(!CopyEngine.SameStorage(@"C:\a", @"D:\a"), "Different drives treated as same");
        });
        Test("0.4.4 parallel verification matches, detects a one-byte difference and works in Compare", () => {
            Func<int, int, byte[]> data = (mb, seed) => { var b = new byte[mb * 1048576]; new Random(seed).NextBytes(b); return b; };
            Action<string, byte[]> put = (p, c) => { Directory.CreateDirectory(Path.GetDirectoryName(p)); File.WriteAllBytes(p, c); File.SetLastWriteTimeUtc(p, Old); };
            var f = new Fixture(); f.Engine.ParallelOnSameStorage = true; f.Engine.ParallelMinimumBytes = 1;
            for (int i = 0; i < 3; i++) put(Path.Combine(f.Source, "f" + i), data(6, i));
            put(Path.Combine(f.Target, "f0"), data(6, 0));
            var bad = data(6, 9); put(Path.Combine(f.Source, "same-size"), bad); var other = (byte[])bad.Clone(); other[other.Length - 3] ^= 0x55; put(Path.Combine(f.Target, "same-size"), other);
            var r = f.Run(OperationMode.VerifiedCopy);
            // 0.4.5: the two copied files are checked by reading only the destination.
            Require(r.Matched == 3 && r.Mismatched == 1 && r.Copied == 2 && f.Engine.ParallelVerifications == 2 && f.Engine.DestinationOnlyChecks == 2, $"matched={r.Matched} mismatched={r.Mismatched} copied={r.Copied} parallel={f.Engine.ParallelVerifications} destination-only={f.Engine.DestinationOnlyChecks}");
            f.Engine.ParallelVerifications = 0; var c = f.Run(OperationMode.Compare);
            Require(c.Matched == 3 && c.Mismatched == 1 && f.Engine.ParallelVerifications == 4, "Compare: matched=" + c.Matched + " parallel=" + f.Engine.ParallelVerifications);
            var seq = new Fixture(); put(Path.Combine(seq.Source, "x"), data(6, 1)); seq.Run(OperationMode.VerifiedCopy);
            Require(seq.Engine.ParallelVerifications == 0, "Same drive should read one after the other by default");
        });
        Test("0.4.4 cancel during parallel verification stops promptly and leaves nothing open", () => {
            var f = new Fixture(); var b = new byte[96 * 1048576]; new Random(4).NextBytes(b);
            File.WriteAllBytes(Path.Combine(f.Source, "big.bin"), b); File.WriteAllBytes(Path.Combine(f.Target, "big.bin"), b);
            File.SetLastWriteTimeUtc(Path.Combine(f.Source, "big.bin"), Old); File.SetLastWriteTimeUtc(Path.Combine(f.Target, "big.bin"), Old);
            f.Engine.ParallelOnSameStorage = true; var cts = new CancellationTokenSource();
            f.Engine.Progress = u => { if (u.Phase == "Verifying source and destination together" && u.FileBytes > 16L * 1048576) cts.Cancel(); };
            var sw = System.Diagnostics.Stopwatch.StartNew(); bool cancelled = false;
            try { f.Engine.Run(f.Plan(), ExistingRule.SkipSameAndOlder, OperationMode.VerifiedCopy, cts.Token); } catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled && sw.ElapsedMilliseconds < 10000, "cancelled=" + cancelled + " after " + sw.ElapsedMilliseconds + " ms");
            using (new FileStream(Path.Combine(f.Target, "big.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            using (new FileStream(Path.Combine(f.Source, "big.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        });
        Test("0.4.4 timing: reading both copies together vs one after the other (info)", () => {
            var f = new Fixture(); var b = new byte[128 * 1048576]; new Random(7).NextBytes(b);
            File.WriteAllBytes(Path.Combine(f.Source, "t.bin"), b); File.WriteAllBytes(Path.Combine(f.Target, "t.bin"), b);
            File.SetLastWriteTimeUtc(Path.Combine(f.Source, "t.bin"), Old); File.SetLastWriteTimeUtc(Path.Combine(f.Target, "t.bin"), Old);
            foreach (bool parallel in new[] { false, true })
            {
                f.Engine.ParallelVerify = parallel; f.Engine.ParallelOnSameStorage = true;
                var sw = System.Diagnostics.Stopwatch.StartNew(); var r = f.Run(OperationMode.Compare);
                Console.WriteLine("INFO 128 MiB compare, " + (parallel ? "both copies together" : "one after the other") + ": " + sw.ElapsedMilliseconds + " ms (same disk; G: to NAS gains more)");
                Require(r.Matched == 1, "Not matched");
            }
        });
        // 0.4.5: source fingerprint taken while copying; checks read the destination past the Windows cache
        Test("0.4.5 uncached reads give the same SHA-256 as normal reads for awkward sizes", () => {
            var f = new Fixture(); var rnd = new Random(11);
            foreach (int size in new[] { 0, 1, 511, 4095, 4096, 4097, 1048575, 1048576, 1048577, 3 * 1048576 + 7 })
            {
                var b = new byte[size]; rnd.NextBytes(b); string p = Path.Combine(f.Target, "s" + size); File.WriteAllBytes(p, b);
                string expected; using (var sha = System.Security.Cryptography.SHA256.Create()) expected = BitConverter.ToString(sha.ComputeHash(b));
                string actual; long length;
                using (var u = new UncachedReader(p)) using (var sha = System.Security.Cryptography.SHA256.Create()) { length = u.Length; actual = BitConverter.ToString(sha.ComputeHash(u)); }
                Require(actual == expected && length == size, "Size " + size + ": uncached read differs");
                using (var u = new UncachedReader(p)) { try { new FileStream(p, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose(); throw new Exception("Writer allowed during uncached read of size " + size); } catch (IOException) { } }
            }
        });
        Test("0.4.5 copied files are checked by reading only the destination, past the cache", () => {
            Action<string, int, int> put = (p, mb, seed) => { var b = new byte[mb * 1048576 + seed]; new Random(seed).NextBytes(b); Directory.CreateDirectory(Path.GetDirectoryName(p)); File.WriteAllBytes(p, b); File.SetLastWriteTimeUtc(p, Old); };
            var f = new Fixture(); for (int i = 0; i < 4; i++) put(Path.Combine(f.Source, "n" + i), 5, 10 + i);
            put(Path.Combine(f.Source, "old"), 5, 20); put(Path.Combine(f.Target, "old"), 5, 20);
            var r = f.Run(OperationMode.VerifiedCopy);
            Require(r.Matched == 5 && r.Copied == 4 && r.Skipped == 1, $"matched={r.Matched} copied={r.Copied}");
            Require(f.Engine.DestinationOnlyChecks == 4 && f.Engine.SourceRereads == 1, $"destination-only={f.Engine.DestinationOnlyChecks} source re-reads={f.Engine.SourceRereads}");
            Require(f.Engine.UncachedReads == 5, "Uncached reads: " + f.Engine.UncachedReads);
            Require(r.Items.Count(x => x.Status == "Matched" && x.Reason.StartsWith("SHA-256 matched: destination read back")) == 4, "Reason text");
        });
        Test("0.4.5 a copy corrupted before the check is caught", () => {
            var f = new Fixture(); var b = new byte[6 * 1048576 + 3]; new Random(30).NextBytes(b); File.WriteAllBytes(Path.Combine(f.Source, "x"), b); File.SetLastWriteTimeUtc(Path.Combine(f.Source, "x"), Old);
            f.Engine.BeforeRead = p => { if (p.StartsWith(f.Target)) { var d = File.ReadAllBytes(p); d[123] ^= 1; var w = File.GetLastWriteTimeUtc(p); File.WriteAllBytes(p, d); File.SetLastWriteTimeUtc(p, w); } };
            var r = f.Run(OperationMode.VerifiedCopy);
            Require(r.Mismatched == 1 && r.Matched == 0 && f.Engine.DestinationOnlyChecks == 1, $"mismatched={r.Mismatched} matched={r.Matched}");
        });
        Test("0.4.5 timing: Verified Copy of new files, source read once vs twice (info)", () => {
            foreach (bool once in new[] { false, true })
            {
                var f = new Fixture(); var b = new byte[96 * 1048576]; new Random(5).NextBytes(b); File.WriteAllBytes(Path.Combine(f.Source, "t.bin"), b);
                f.Engine.UncachedVerify = false; if (!once) f.Engine.BeforeCommit = p => { };
                var sw = System.Diagnostics.Stopwatch.StartNew();
                if (!once) { f.Engine.ParallelVerify = false; }
                var r = once ? f.Run(OperationMode.VerifiedCopy) : f.Engine.Run(f.Plan(), ExistingRule.SkipSameAndOlder, OperationMode.FastCopy, CancellationToken.None);
                if (!once) { f.Engine.Run(f.Plan(), ExistingRule.SkipSameAndOlder, OperationMode.Compare, CancellationToken.None); }
                Console.WriteLine("INFO 96 MiB new file, " + (once ? "0.4.5 copy + destination check" : "copy then full two-file check (as before)") + ": " + sw.ElapsedMilliseconds + " ms");
            }
        });
        // 0.4.6: several files at once; whole-job breakdown of results
        Func<Fixture> mixedTree = () => {
            var f = new Fixture(); var rnd = new Random(1);
            for (int i = 0; i < 300; i++) { var b = new byte[rnd.Next(0, 30000)]; rnd.NextBytes(b); string p = Path.Combine(f.Source, "d" + (i % 7), "e" + (i % 3), "f" + i + ".dat"); Directory.CreateDirectory(Path.GetDirectoryName(p)); File.WriteAllBytes(p, b); File.SetLastWriteTimeUtc(p, Old); }
            var big = new byte[20 * 1048576]; rnd.NextBytes(big); File.WriteAllBytes(Path.Combine(f.Source, "large.bin"), big); File.SetLastWriteTimeUtc(Path.Combine(f.Source, "large.bin"), Old);
            for (int i = 0; i < 60; i++) { string p = Path.Combine(f.Source, "d" + (i % 7), "e" + (i % 3), "f" + i + ".dat"); string t = p.Replace(f.Source, f.Target); Directory.CreateDirectory(Path.GetDirectoryName(t)); File.Copy(p, t); File.SetLastWriteTimeUtc(t, Old); }
            string bad = Path.Combine(f.Target, "d1", "e1", "f1.dat"); var bb = File.ReadAllBytes(bad); if (bb.Length > 0) { bb[0] ^= 1; File.WriteAllBytes(bad, bb); File.SetLastWriteTimeUtc(bad, Old); }
            return f; };
        Func<Result, string> sig = r => $"copied={r.Copied} skipped={r.Skipped} matched={r.Matched} mismatched={r.Mismatched} errors={r.Errors} missing={r.Missing} unchecked={r.Unchecked} changed={r.Changed} created={r.FoldersCreated} present={r.FoldersPresent} processed={r.FilesProcessed}";
        Test("0.4.6 4 files at once gives exactly the same results as 1 at a time", () => {
            foreach (var mode in new[] { OperationMode.VerifiedCopy, OperationMode.FastCopy })
            {
                var one = mixedTree(); var four = mixedTree(); four.Engine.FilesAtOnce = 4;
                var sw = System.Diagnostics.Stopwatch.StartNew(); var a = one.Run(mode); long t1 = sw.ElapsedMilliseconds; sw.Restart(); var b = four.Run(mode); long t4 = sw.ElapsedMilliseconds;
                Console.WriteLine("INFO " + mode + " 301 files: 1 at a time " + t1 + " ms, 4 at once " + t4 + " ms (local disk; network gains more)");
                Require(sig(a) == sig(b), mode + ": " + sig(a) + " vs " + sig(b));
                Require(!Directory.GetFiles(four.Target, "*.partial", SearchOption.AllDirectories).Any(), "Partial file left");
                foreach (string src in Directory.GetFiles(four.Source, "*", SearchOption.AllDirectories)) Require(File.Exists(src.Replace(four.Source, four.Target)), "Missing " + src);
            }
            var cmp1 = mixedTree(); cmp1.Run(OperationMode.VerifiedCopy); var c1 = cmp1.Run(OperationMode.Compare); cmp1.Engine.FilesAtOnce = 3; var c3 = cmp1.Run(OperationMode.Compare);
            Require(sig(c1) == sig(c3) && c3.Mismatched == 1, "Compare: " + sig(c1) + " vs " + sig(c3));
        });
        Test("0.4.6 cancel with 4 files at once: every file accounted for, nothing half-written, rerun clean", () => {
            var f = mixedTree(); f.Engine.FilesAtOnce = 4; var cts = new CancellationTokenSource(); var rows = new System.Collections.Concurrent.ConcurrentBag<ItemResult>(); int fired = 0;
            f.Engine.ItemCompleted = rows.Add; f.Engine.Progress = u => { if (u.Done >= 80 && Interlocked.Increment(ref fired) == 1) cts.Cancel(); };
            bool cancelled = false; try { f.Engine.Run(f.Plan(), ExistingRule.SkipSameAndOlder, OperationMode.VerifiedCopy, cts.Token); } catch (OperationCanceledException) { cancelled = true; }
            int accounted = rows.Where(x => !x.Directory).Select(x => x.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            Console.WriteLine("INFO cancelled after " + rows.Count(x => x.Status == "Matched" || x.Status == "Mismatch") + " checks; not reached " + rows.Count(x => x.Status == "Not reached"));
            Require(cancelled && accounted == 301, "Cancelled=" + cancelled + ", files accounted " + accounted + " of 301");
            Require(!Directory.GetFiles(f.Target, "*.partial", SearchOption.AllDirectories).Any(), "Partial file left");
            f.Engine.Progress = _ => { }; f.Engine.ItemCompleted = _ => { };
            var again = f.Run(OperationMode.VerifiedCopy); Require(again.Errors == 0 && again.Mismatched == 1 && again.Matched == 300, "Rerun: " + sig(again));
        });
        Test("0.4.6 network outage with 4 files at once stops cleanly and resumes", () => {
            var f = mixedTree(); f.Engine.FilesAtOnce = 4; f.Engine.OutageFirstWaitMs = 20; f.Engine.OutageMaxWaitMs = 50; f.Engine.OutageWaitLimit = TimeSpan.FromMilliseconds(300);
            f.Engine.BeforeCopy = src => { if (src.EndsWith("f150.dat")) throw netError(); };
            Exception stopped = null; try { f.Engine.Run(f.Plan(), ExistingRule.SkipSameAndOlder, OperationMode.VerifiedCopy, CancellationToken.None); } catch (Exception ex) { stopped = ex; }
            Require(stopped is NetworkOutageException, "Not a network stop: " + stopped);
            Require(!Directory.GetFiles(f.Target, "*.partial", SearchOption.AllDirectories).Any(), "Partial file left");
            f.Engine.BeforeCopy = _ => { };
            var again = f.Run(OperationMode.VerifiedCopy); Require(again.Errors == 0 && again.Mismatched == 1 && again.Matched == 300, "Resume: " + sig(again));
        });
        Test("0.4.6 Pause holds every file worker", () => {
            var f = mixedTree(); f.Engine.FilesAtOnce = 4; int during = 0; bool paused = false; f.Engine.Progress = u => { if (paused) Interlocked.Increment(ref during); };
            var th = new Thread(() => f.Run(OperationMode.FastCopy)); th.Start(); Thread.Sleep(100); f.Engine.Gate.Reset(); Thread.Sleep(400); paused = true; Thread.Sleep(700); int seen = during; f.Engine.Gate.Set(); th.Join();
            Require(seen < 10, "Workers kept going while paused: " + seen + " updates");
        });
        Test("0.4.6 Results: one row per file with 4 at once, whole-job breakdown, Show filter and report", () => {
            var f = mixedTree(); f.Put(true, Path.Combine("d0", "e0", "newer.txt"), "NEWER", New); f.Put(false, Path.Combine("d0", "e0", "newer.txt"), "older", Old);
            var w = TestWindow(f); var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            ((ComboBox)Field(w, "mode")).SelectedIndex = (int)OperationMode.VerifiedCopy;
            Require(((ComboBox)Field(w, "filesAtOnce")).SelectedItem.ToString() == "4 files at once", "Default is not 4 files at once");
            Invoke(w, "Start", false); var job = WaitJob(w);
            Require(job.FilesAtOnce == 4 && File.ReadAllText((string)Field(w, "logPath")).Contains("files-at-once=4"), "Setting not saved/logged");
            // The job has issues, so ClearCopy ticks "Issues only" at the end; untick it to see every row.
            ((CheckBox)Field(w, "issuesOnly")).IsChecked = false; System.Windows.Data.CollectionViewSource.GetDefaultView(Field(w, "results")).Refresh();
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(Field(w, "results")).Cast<ItemResult>().Where(x => !x.Directory && !x.Target.EndsWith("newer.txt")).ToList();
            Require(view.Count == 301 && view.Select(x => x.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 301, "Rows per file: " + view.Count);
            Require(view.Count(x => x.GridStatus == "Existing file — verified") == 59 && view.Count(x => x.GridStatus == "Copied — verified") == 241 && view.Count(x => x.GridStatus == "Existing file DIFFERS") == 1, "Merged rows: " + string.Join(", ", view.GroupBy(x => x.GridStatus).Select(g => g.Key + "=" + g.Count())));
            var links = (Dictionary<string, Button>)Field(w, "countButtons");
            links["Skipped"].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            string text = ((TextBlock)Field(w, "statusFilterText")).Text;
            Require(text.Contains("Whole job: 60 skipped — already there, same size and date") && text.Contains("1 kept — newer file on destination"), "Breakdown: " + text);
            Invoke(w, "ShowKind", "Skipped — already there, same size and date");
            var kind = System.Windows.Data.CollectionViewSource.GetDefaultView(Field(w, "results")).Cast<ItemResult>().ToList();
            Require(kind.Count == 60, "Show filter rows: " + kind.Count);
            Invoke(w, "FillResultKinds"); var combo = (ComboBox)Field(w, "resultKind");
            Require(combo.Items.Cast<ComboBoxItem>().Any(i => ((string)i.Content).StartsWith("Skipped — already there, same size and date  (60)")), "Show list");
            string report = File.ReadAllText((string)Field(w, "logPath") + ".summary.txt");
            Require(report.Contains("Results by kind (whole job, every row):") && report.Contains("Skipped — already there, same size and date: 60") && report.Contains("Kept — newer file on destination: 1"), "Report breakdown missing");
            w.Close();
        });
        Console.WriteLine("RESULT passed=" + passed + " failed=" + failed);
        return failed == 0 ? 0 : 1;
    }
}
