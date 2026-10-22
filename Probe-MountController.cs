using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

// Read-only diagnostic. Does not call Start, TryStop, core/quit or mount APIs.
internal static class ProbeMountController
{
    public static int Main(string[] args)
    {
        string directory = args.Length > 0 ? args[0] : AppDomain.CurrentDomain.BaseDirectory;
        MountSettings settings = null;
        MountController controller = null;
        Probe("load settings", delegate {
            settings = MountSettings.Load(directory);
            return "loaded";
        }, 8000);
        Probe("construct controller", delegate {
            controller = new MountController(directory);
            return "constructed";
        }, 8000);
        if (controller != null)
            Probe("GetStatus on MTA worker", delegate {
                MountSnapshot status = controller.GetStatus();
                return "State=" + status.State + "; Detail=" + status.Detail;
            }, 8000);
        if (settings != null)
        {
            Probe("isolated Scheduled Task read on MTA worker", delegate {
                MountTaskInfo task = new WindowsMountEnvironment(settings).ReadTask();
                return task == null ? "task missing" : "State=" + task.State + "; Enabled=" + task.Enabled;
            }, 8000);
            Probe("isolated authenticated vfs/stats read", delegate {
                IDictionary<string, object> result = new WindowsMountEnvironment(settings).Call("vfs/stats", new Dictionary<string, object>());
                return result == null ? "null response" : "received response object";
            }, 8000);
        }
        Console.WriteLine("Read-only probe finished. No mount changes requested.");
        return 0;
    }

    private static void Probe(string name, Func<string> operation, int timeoutMs)
    {
        var elapsed = Stopwatch.StartNew();
        Console.WriteLine("START " + name);
        Task<string> task = Task.Factory.StartNew(operation, System.Threading.CancellationToken.None,
            TaskCreationOptions.None, TaskScheduler.Default);
        try
        {
            if (!task.Wait(timeoutMs))
            {
                Console.WriteLine("TIMEOUT {0} after {1} ms", name, elapsed.ElapsedMilliseconds);
                return;
            }
            Console.WriteLine("DONE {0} in {1} ms: {2}", name, elapsed.ElapsedMilliseconds, task.Result);
        }
        catch (AggregateException error)
        {
            Exception cause = error.GetBaseException();
            Console.WriteLine("ERROR {0} in {1} ms: {2}: {3}", name, elapsed.ElapsedMilliseconds,
                cause.GetType().Name, cause.Message);
        }
    }
}
