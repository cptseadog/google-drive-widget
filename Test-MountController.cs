using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

// Compile this with MountController.cs into a separate test executable.
// Every mount, network, task and log operation is faked. No real mount changes.
internal static class TestMountController
{
    private static int passed, failed;

    public static int Main()
    {
        Run("missing task is Unknown and cannot start", delegate {
            var f = new Fixture(); f.Backend.Task = null; f.Backend.Absent = true;
            Equal("Unknown", f.Controller.GetStatus().State);
            Throws<InvalidOperationException>(delegate { f.Controller.Start(); });
            Equal(0, f.Backend.Starts);
        });
        Run("disabled task is Unknown even with no endpoint", delegate {
            var f = new Fixture(); f.Backend.Task.Enabled = false; f.Backend.Absent = true;
            Equal("Unknown", f.Controller.GetStatus().State);
            Throws<InvalidOperationException>(delegate { f.Controller.TryStop(f.Requested); });
            Equal(0, f.Backend.Quits);
        });
        Run("authentication failure is never Off", delegate {
            var f = new Fixture(); f.Backend.Task.State = 3;
            f.Backend.EndpointError = new InvalidOperationException("Authentication failed.");
            Equal("Unknown", f.Controller.GetStatus().State);
        });
        Run("endpoint timeout is never Off", delegate {
            var f = new Fixture(); f.Backend.Task.State = 3;
            f.Backend.EndpointError = new IOException("Timeout.");
            Equal("Unknown", f.Controller.GetStatus().State);
        });
        Run("running task without control is Unknown", delegate {
            var f = new Fixture(); f.Backend.Absent = true;
            Equal("Unknown", f.Controller.GetStatus().State);
        });
        Run("queued task reports Starting", delegate {
            var f = new Fixture(); f.Backend.Task.State = 2; f.Backend.Absent = true;
            Equal("Starting", f.Controller.GetStatus().State);
        });
        Run("orphan endpoint is Unknown", delegate {
            var f = new Fixture(); f.Backend.Task.State = 3;
            Equal("Unknown", f.Controller.GetStatus().State);
        });
        Run("leftover mount path prevents Off", delegate {
            var f = new Fixture(); f.Backend.Task.State = 3; f.Backend.Absent = true; f.Backend.PathPresent = true;
            Equal("Unknown", f.Controller.GetStatus().State);
            Throws<InvalidOperationException>(delegate { f.Controller.TryStop(f.Requested); });
        });
        Run("idle Off does not poll the filesystem", delegate {
            var f = new Fixture(); f.Backend.Task.State = 3; f.Backend.Absent = true;
            Equal("Off", f.Controller.GetStatus().State);
            Equal("Off", f.Controller.GetStatus().State);
            Equal(1, f.Backend.PathChecks);
            Equal(0, f.Backend.LogReads);
        });
        Run("Start rechecks root and starts only once", delegate {
            var f = new Fixture(); f.Backend.Task.State = 3; f.Backend.Absent = true;
            Equal("Off", f.Controller.GetStatus().State);
            f.Controller.Start(); f.Controller.Start();
            Equal(1, f.Backend.Starts);
            Equal(2, f.Backend.PathChecks);
        });
        Run("new obstacle after Off prevents Start", delegate {
            var f = new Fixture(); f.Backend.Task.State = 3; f.Backend.Absent = true;
            Equal("Off", f.Controller.GetStatus().State);
            f.Backend.PathPresent = true;
            Throws<InvalidOperationException>(delegate { f.Controller.Start(); });
            Equal(0, f.Backend.Starts);
        });
        Run("wrong remote prevents status and stopping", delegate {
            var f = new Fixture(); f.Backend.Stats["fs"] = "unrelated:";
            Equal("Unknown", f.Controller.GetStatus().State);
            Throws<InvalidDataException>(delegate { f.Controller.TryStop(f.Requested); });
            Equal(0, f.Backend.Quits);
        });
        Run("missing cache fields fail closed", delegate {
            var f = new Fixture(); f.Backend.Cache.Remove("uploadsQueued");
            Equal("Unknown", f.Controller.GetStatus().State);
            Throws<InvalidDataException>(delegate { f.Controller.TryStop(f.Requested); });
        });
        Run("negative and noninteger counts fail closed", delegate {
            var f = new Fixture(); f.Backend.Cache["uploadsQueued"] = -1;
            Throws<InvalidDataException>(delegate { f.Controller.TryStop(f.Requested); });
            f.Backend.Cache["uploadsQueued"] = "0";
            Throws<InvalidDataException>(delegate { f.Controller.TryStop(f.Requested); });
        });
        Run("cached errors prevent shutdown", delegate {
            var f = new Fixture(); f.Backend.Cache["erroredFiles"] = 1;
            Throws<InvalidOperationException>(delegate { f.Controller.TryStop(f.Requested); });
            Equal(0, f.Backend.Quits);
        });
        Run("queue and active uploads prevent shutdown", delegate {
            var f = new Fixture(); f.Backend.Cache["uploadsQueued"] = 2;
            Contains("Finishing uploads", f.Controller.TryStop(f.Requested));
            f.Backend.Cache["uploadsQueued"] = 0; f.Backend.Cache["uploadsInProgress"] = 1;
            Contains("Finishing uploads", f.Controller.TryStop(f.Requested));
            Equal(0, f.Backend.Quits);
        });
        Run("nonempty detailed queue overrides zero stats", delegate {
            var f = new Fixture(); f.Backend.Queue = new object[] { new Dictionary<string, object>() };
            Contains("Finishing uploads", f.Controller.TryStop(f.Requested));
            Equal(0, f.Backend.Quits);
        });
        Run("missing detailed queue fails closed", delegate {
            var f = new Fixture(); f.Backend.Queue = null;
            Throws<InvalidDataException>(delegate { f.Controller.TryStop(f.Requested); });
            Equal(0, f.Backend.Quits);
        });
        Run("cache activity protects unqueued open or dirty files", delegate {
            var f = new Fixture(); f.Backend.Log = Log(f.Now, 1, 0, 0);
            Contains("Waiting for file activity", f.Controller.TryStop(f.Requested));
            Equal(0, f.Backend.Quits);
        });
        Run("log at or before click is not fresh", delegate {
            var f = new Fixture(); f.Backend.Log = Log(f.Requested, 0, 0, 0);
            Contains("fresh", f.Controller.TryStop(f.Requested));
            Equal(0, f.Backend.Quits);
        });
        Run("stale log cannot authorize shutdown", delegate {
            var f = new Fixture(); f.Now = f.Requested.AddSeconds(40);
            Contains("fresh", f.Controller.TryStop(f.Requested));
            Equal(0, f.Backend.Quits);
        });
        Run("future log cannot authorize shutdown", delegate {
            var f = new Fixture(); f.Backend.Log = Log(f.Now.AddSeconds(10), 0, 0, 0);
            Throws<InvalidDataException>(delegate { f.Controller.TryStop(f.Requested); });
            Equal(0, f.Backend.Quits);
        });
        Run("cleaner tick during control calls is not a future timestamp", delegate {
            var f = new Fixture();
            f.Backend.BeforeLogRead = delegate {
                f.Now = f.Now.AddSeconds(2); f.Backend.Log = Log(f.Now, 0, 0, 0);
            };
            Contains("Checking", f.Controller.TryStop(f.Requested));
            Equal(0, f.Backend.Quits);
        });
        Run("malformed latest record never falls back to old zero", delegate {
            var f = new Fixture(); f.Backend.Log += "2026/10/22 13:00:01 INFO : vfs cache: cleaned: unparseable\n";
            Throws<InvalidDataException>(delegate { f.Controller.TryStop(f.Requested); });
            Equal(0, f.Backend.Quits);
        });
        Run("partial newest summary cannot reuse old zero", delegate {
            var f = new Fixture(); f.Backend.Log += "2026/10/22 13:00:01 INFO : vfs cache: cleaned: objects";
            Contains("fresh", f.Controller.TryStop(f.Requested));
            Equal(0, f.Backend.Quits);
        });
        Run("two reads of the same cleaner record do not stop", delegate {
            var f = new Fixture(); f.Controller.TryStop(f.Requested);
            f.Now = f.Now.AddSeconds(3);
            Contains("next", f.Controller.TryStop(f.Requested));
            Equal(0, f.Backend.Quits);
        });
        Run("independent samples must be at least two seconds apart", delegate {
            var f = new Fixture(); f.Controller.TryStop(f.Requested);
            f.Now = f.Now.AddSeconds(1); f.Backend.Log = Log(f.Now, 0, 0, 0);
            Contains("Checking", f.Controller.TryStop(f.Requested));
            Equal(0, f.Backend.Quits);
        });
        Run("two independent idle samples request one graceful quit", delegate {
            var f = new Fixture(); f.Controller.TryStop(f.Requested);
            f.Now = f.Now.AddSeconds(10); f.Backend.Log = Log(f.Now, 0, 0, 0);
            Equal("Stopping", f.Controller.TryStop(f.Requested));
            Equal("Stopping", f.Controller.TryStop(f.Requested));
            Equal(1, f.Backend.Quits);
            Equal(0, f.Backend.QuitExitCode);
        });
        Run("busy sample resets the stability window", delegate {
            var f = new Fixture(); f.Controller.TryStop(f.Requested);
            f.Now = f.Now.AddSeconds(10); f.Backend.Cache["uploadsQueued"] = 1;
            f.Controller.TryStop(f.Requested);
            f.Now = f.Now.AddSeconds(10); f.Backend.Cache["uploadsQueued"] = 0; f.Backend.Log = Log(f.Now, 0, 0, 0);
            Contains("Checking", f.Controller.TryStop(f.Requested));
            Equal(0, f.Backend.Quits);
        });
        Run("new Off request cannot reuse earlier idle sample", delegate {
            var f = new Fixture(); f.Controller.TryStop(f.Requested);
            f.Now = f.Now.AddSeconds(10); f.Backend.Log = Log(f.Now, 0, 0, 0);
            Contains("Checking", f.Controller.TryStop(f.Now.AddSeconds(-1)));
            Equal(0, f.Backend.Quits);
        });
        Run("task restart invalidates earlier idle sample", delegate {
            var f = new Fixture(); f.Controller.TryStop(f.Requested);
            f.Now = f.Now.AddSeconds(10); f.Backend.Task.LastRunUtc = f.Now.AddSeconds(-2);
            f.Backend.Log = Log(f.Now, 0, 0, 0);
            Contains("Checking", f.Controller.TryStop(f.Requested));
            Equal(0, f.Backend.Quits);
        });
        Run("log parses local timestamps into UTC and accepts microseconds", delegate {
            var f = new Fixture();
            string line = Log(f.Now, 3, 4, 5).Replace(" INFO", ".123456 INFO");
            CacheActivitySummary result = MountController.ParseLatestSummary(line);
            Equal(f.Now, result.TimestampUtc); Equal(3, result.InUse); Equal(4, result.Queued); Equal(5, result.Uploading);
        });
        Run("GetStatus does not use VFS inUse as a handle count", delegate {
            var f = new Fixture(); f.Backend.Stats["inUse"] = 1;
            Equal("On", f.Controller.GetStatus().State);
            Equal(0, f.Backend.LogReads); Equal(0, f.Backend.PathChecks);
        });
        Console.WriteLine("{0} passed; {1} failed. No real task, mount, HTTP or cloud operations were performed.", passed, failed);
        return failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception("Expected " + expected + "; got " + actual + ".");
    }
    private static void Contains(string expected, string actual)
    {
        if (actual.IndexOf(expected, StringComparison.Ordinal) < 0) throw new Exception("Expected message containing " + expected + ".");
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name + ".");
    }
    private static string Log(DateTime utc, int used, int queued, int uploading)
    {
        return utc.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture) +
            " INFO : Google drive root '': vfs cache: cleaned: objects 5 (was 5) in use " + used +
            ", to upload " + queued + ", uploading " + uploading + ", total size 5 MiB (was 5 MiB)\r\n";
    }

    private sealed class Fixture
    {
        internal readonly DateTime Requested = new DateTime(2026, 10, 22, 17, 0, 0, DateTimeKind.Utc);
        internal DateTime Now;
        internal readonly FakeEnvironment Backend = new FakeEnvironment();
        internal readonly MountController Controller;
        internal Fixture()
        {
            Now = Requested.AddSeconds(1);
            Backend.Task.LastRunUtc = Requested.AddMinutes(-10);
            Backend.Log = Log(Now, 0, 0, 0);
            Controller = new MountController(new MountSettings { ExpectedRemote = "gdrive:", MountPath = @"D:\dev\GoogleDrive" },
                Backend, delegate { return Now; });
        }
    }

    private sealed class FakeEnvironment : IMountEnvironment
    {
        internal MountTaskInfo Task = new MountTaskInfo { Enabled = true, State = 4 };
        internal bool Absent, PathPresent;
        internal Exception EndpointError;
        internal Action BeforeLogRead;
        internal int Starts, Quits, QuitExitCode = -1, PathChecks, LogReads;
        internal string Log;
        internal object Queue = new object[0];
        internal readonly Dictionary<string, object> Cache = new Dictionary<string, object> {
            { "uploadsQueued", 0 }, { "uploadsInProgress", 0 }, { "erroredFiles", 0 }
        };
        internal readonly Dictionary<string, object> Stats;
        internal FakeEnvironment() { Stats = new Dictionary<string, object> { { "fs", "gdrive:" }, { "diskCache", Cache } }; }
        public MountTaskInfo ReadTask() { return Task; }
        public void RunTask() { Starts++; Task.State = 2; }
        public string ReadLogTail() { LogReads++; if (BeforeLogRead != null) BeforeLogRead(); return Log; }
        public bool MountPathPresent() { PathChecks++; return PathPresent; }
        public IDictionary<string, object> Call(string route, IDictionary<string, object> input)
        {
            if (EndpointError != null) throw EndpointError;
            if (Absent) throw new MountEndpointAbsentException();
            if (route == "vfs/stats") return Stats;
            if (route == "vfs/queue") return Queue == null ? new Dictionary<string, object>() :
                new Dictionary<string, object> { { "queue", Queue } };
            if (route == "core/quit")
            {
                Quits++; QuitExitCode = (int)input["exitCode"];
                return new Dictionary<string, object>();
            }
            throw new Exception("Unexpected fake route: " + route);
        }
    }
}
