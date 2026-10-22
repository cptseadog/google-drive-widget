using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

public sealed class MountSnapshot
{
    public string State { get; set; }
    public string Detail { get; set; }
    public int UploadsQueued { get; set; }
    public int UploadsInProgress { get; set; }
}

internal sealed class MountSettings
{
    internal string TaskName, Endpoint, MountPath, LogPath, CredentialPath, ExpectedRemote, Username;

    internal static MountSettings Load(string baseDirectory)
    {
        string path = Path.Combine(Path.GetFullPath(baseDirectory), "settings.json");
        if (new FileInfo(path).Length > 32768) throw new InvalidDataException("Widget settings are too large.");
        var json = new JavaScriptSerializer();
        var values = json.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as IDictionary<string, object>;
        if (values == null) throw new InvalidDataException("Widget settings must be a JSON object.");
        var settings = new MountSettings {
            TaskName = Required(values, "TaskName"), Endpoint = Required(values, "Endpoint"),
            MountPath = Required(values, "MountPath"), LogPath = Required(values, "LogPath"),
            CredentialPath = Required(values, "CredentialPath"), ExpectedRemote = Required(values, "ExpectedRemote"),
            Username = Required(values, "Username")
        };
        Uri endpoint;
        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out endpoint) ||
            endpoint.Scheme != "http" || endpoint.Host != "127.0.0.1" ||
            endpoint.AbsolutePath != "/" || endpoint.UserInfo.Length != 0 ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new InvalidDataException("Mount control must use an HTTP endpoint on 127.0.0.1 with no path or credentials.");
        if (!Path.IsPathRooted(settings.MountPath) || !Path.IsPathRooted(settings.LogPath) ||
            !Path.IsPathRooted(settings.CredentialPath))
            throw new InvalidDataException("Widget file paths must be absolute.");
        if (settings.Username.IndexOf(':') >= 0 || settings.Username.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            throw new InvalidDataException("Invalid mount control username.");
        return settings;
    }

    private static string Required(IDictionary<string, object> values, string name)
    {
        object value;
        if (!values.TryGetValue(name, out value) || !(value is string) || String.IsNullOrWhiteSpace((string)value))
            throw new InvalidDataException("Missing widget setting: " + name + ".");
        return (string)value;
    }
}

internal sealed class MountTaskInfo
{
    internal bool Enabled;
    // Task Scheduler: 0 unknown, 1 disabled, 2 queued, 3 ready, 4 running.
    internal int State;
    internal DateTime LastRunUtc;
}

internal interface IMountEnvironment
{
    MountTaskInfo ReadTask();
    void RunTask();
    IDictionary<string, object> Call(string route, IDictionary<string, object> input);
    string ReadLogTail();
    bool MountPathPresent();
}

internal sealed class MountEndpointAbsentException : IOException
{
    internal MountEndpointAbsentException() : base("The mount control endpoint is not listening.") { }
}

internal sealed class CacheActivitySummary
{
    internal DateTime TimestampUtc;
    internal int InUse, Queued, Uploading;
}

internal sealed class MountStatistics
{
    internal int Queued, Uploading, Errors;
}

public sealed class MountController
{
    private readonly object sync = new object();
    private readonly MountSettings settings;
    private readonly IMountEnvironment environment;
    private readonly Func<DateTime> utcNow;
    private DateTime? startRequestedUtc, stopRequestUtc, idleSinceUtc, idleTaskRunUtc, idleSummaryUtc;
    private bool quitSent, offVerified;
    private const int FreshSummarySeconds = 25; // Installer uses a 10-second cache cleaner interval.
    private static readonly Regex SummaryPattern = new Regex(
        @"^\s*(?<stamp>\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2})(?:\.\d+)?\s+.*vfs cache: cleaned:.*\bin use (?<used>\d+), to upload (?<queued>\d+), uploading (?<uploading>\d+),",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public MountController(string baseDirectory)
    {
        settings = MountSettings.Load(baseDirectory);
        environment = new WindowsMountEnvironment(settings);
        utcNow = delegate { return DateTime.UtcNow; };
    }

    // Test seam: unit tests use fake scheduling, HTTP, log and clock providers.
    internal MountController(MountSettings configuration, IMountEnvironment backend, Func<DateTime> clock)
    {
        settings = configuration;
        environment = backend;
        utcNow = clock;
    }

    public string MountPath { get { return settings.MountPath; } }

    public MountSnapshot GetStatus()
    {
        lock (sync)
        {
            try { return ReadStatus(); }
            catch (Exception ex)
            {
                ResetIdle();
                offVerified = false;
                return Snapshot("Unknown", ex.Message, -1, -1);
            }
        }
    }

    private MountSnapshot ReadStatus()
    {
        MountTaskInfo task = RequireTask();
        MountStatistics stats = null;
        bool absent = false;
        try { stats = ReadStatistics(); }
        catch (MountEndpointAbsentException) { absent = true; }

        if (task.State == 3 && absent)
        {
            VerifyOffPath();
            startRequestedUtc = null;
            ResetIdle();
            return Snapshot("Off", "Google Drive is not mounted.", 0, 0);
        }
        offVerified = false;
        if (task.State == 4 && stats != null)
        {
            string detail = stats.Errors != 0 ? "Google Drive is mounted; cached file errors need attention." :
                (stats.Queued + (long)stats.Uploading > 0 ? "Google Drive is mounted; uploads are pending." : "Google Drive is mounted.");
            return Snapshot("On", detail, stats.Queued, stats.Uploading);
        }
        if (task.State == 2 && absent)
            return Snapshot("Starting", "The mount task is queued.", -1, -1);
        if (task.State == 4 && absent && quitSent && stopRequestUtc.HasValue &&
            utcNow() - stopRequestUtc.Value < TimeSpan.FromMinutes(3))
            return Snapshot("Starting", "Disconnecting Google Drive…", -1, -1);
        if (task.State == 4 && absent && startRequestedUtc.HasValue &&
            utcNow() - startRequestedUtc.Value < TimeSpan.FromSeconds(45))
            return Snapshot("Starting", "Waiting for Google Drive to become ready…", -1, -1);

        ResetIdle();
        return Snapshot("Unknown", absent ? "The mount task is running, but its control endpoint is unavailable." :
            "Mount control is responding, but the scheduled task is not running.", -1, -1);
    }

    public void Start()
    {
        lock (sync)
        {
            offVerified = false; // Recheck the root at this explicit transition.
            MountSnapshot current = ReadStatus();
            if (current.State == "On" || current.State == "Starting") return;
            if (current.State != "Off") throw new InvalidOperationException(current.Detail);
            ResetIdle();
            quitSent = false;
            stopRequestUtc = null;
            environment.RunTask();
            startRequestedUtc = utcNow();
        }
    }

    public string TryStop(DateTime requestedUtc)
    {
        lock (sync)
        {
            try { return TryStopCore(requestedUtc); }
            catch { ResetIdle(); throw; }
        }
    }

    private string TryStopCore(DateTime requestedUtc)
    {
        if (requestedUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("The stop request time must be UTC.", "requestedUtc");
        DateTime now = utcNow();
        if (requestedUtc > now) throw new ArgumentException("The stop request time is in the future.", "requestedUtc");
        if (!stopRequestUtc.HasValue || stopRequestUtc.Value != requestedUtc)
        {
            stopRequestUtc = requestedUtc;
            quitSent = false;
            ResetIdle();
        }
        if (quitSent) return "Stopping";

        MountTaskInfo task = RequireTask();
        MountStatistics stats;
        try { stats = ReadStatistics(); }
        catch (MountEndpointAbsentException)
        {
            ResetIdle();
            if (task.State == 3) { VerifyOffPath(); return "Off"; }
            throw new InvalidOperationException("Cannot safely stop: the running mount's control endpoint is unavailable.");
        }
        if (task.State != 4)
            throw new InvalidOperationException("Cannot safely stop: the mount task is not in the running state.");
        offVerified = false;

        if (stats.Errors > 0)
            throw new InvalidOperationException("Cached file errors need attention. Google Drive has been left mounted.");
        bool queueEmpty = QueueIsEmpty(environment.Call("vfs/queue", EmptyInput()));
        if (stats.Queued != 0 || stats.Uploading != 0 || !queueEmpty)
            return Wait("Finishing uploads…");

        CacheActivitySummary activity = ParseLatestSummary(environment.ReadLogTail());
        // HTTP calls may span a cleaner tick; compare against time AFTER the
        // log read so a newly appended current record is not mistaken for a
        // future timestamp.
        now = utcNow();
        if (activity == null || activity.TimestampUtc <= requestedUtc ||
            activity.TimestampUtc < task.LastRunUtc || now - activity.TimestampUtc > TimeSpan.FromSeconds(FreshSummarySeconds))
            return Wait("Waiting for a fresh file activity check…");
        if (activity.TimestampUtc > now)
            throw new InvalidDataException("The activity log timestamp is in the future; Google Drive has been left mounted.");
        if (activity.InUse != 0 || activity.Queued != 0 || activity.Uploading != 0)
            return Wait("Waiting for file activity to finish. Save and close Drive files…");

        // The cleaner's 'in use' counts opened cache items OR dirty items. It is
        // NOT vfs/stats.inUse (the VFS reference count) and is NOT an exhaustive
        // Windows handle count. Deferred opens and new activity after a sample
        // remain possible. Users must save/close editors before disconnecting.
        // We preserve the cache; there is deliberately no force-kill fallback.
        if (!idleSinceUtc.HasValue || !idleTaskRunUtc.HasValue || idleTaskRunUtc.Value != task.LastRunUtc || now < idleSinceUtc.Value)
        {
            idleSinceUtc = now;
            idleTaskRunUtc = task.LastRunUtc;
            idleSummaryUtc = activity.TimestampUtc;
            return "Checking that file activity has finished…";
        }
        if (now - idleSinceUtc.Value < TimeSpan.FromSeconds(2))
            return "Checking that file activity has finished…";
        // Require an independently recorded second cleaner sample, not repeated
        // reads of the same log line while an unqueued file could be changing.
        if (!idleSummaryUtc.HasValue || activity.TimestampUtc <= idleSummaryUtc.Value)
            return "Waiting for the next file activity check…";

        // The second sample just rechecked task, current queue/stats and a fresh
        // cleaner record. core/quit runs rclone's registered unmount cleanup;
        // it does not itself drain uploads, which is why the guards precede it.
        environment.Call("core/quit", new Dictionary<string, object> { { "exitCode", 0 } });
        quitSent = true;
        ResetIdle();
        return "Stopping";
    }

    private MountTaskInfo RequireTask()
    {
        MountTaskInfo task = environment.ReadTask();
        if (task == null) throw new InvalidOperationException("The Google Drive mount task is missing.");
        if (!task.Enabled || task.State == 1) throw new InvalidOperationException("The Google Drive mount task is disabled.");
        if (task.State < 2 || task.State > 4) throw new InvalidOperationException("The Google Drive mount task state is unknown.");
        return task;
    }

    private MountStatistics ReadStatistics()
    {
        return ParseStatistics(environment.Call("vfs/stats", EmptyInput()), settings.ExpectedRemote);
    }

    internal static MountStatistics ParseStatistics(IDictionary<string, object> response, string expectedRemote)
    {
        object fs, rawCache;
        if (response == null || !response.TryGetValue("fs", out fs) || !(fs is string) ||
            !String.Equals((string)fs, expectedRemote, StringComparison.Ordinal))
            throw new InvalidDataException("The mount control endpoint does not identify the expected Google Drive remote.");
        if (!response.TryGetValue("diskCache", out rawCache) || !(rawCache is IDictionary<string, object>))
            throw new InvalidDataException("Mount control did not provide disk cache status.");
        var cache = (IDictionary<string, object>)rawCache;
        return new MountStatistics { Queued = RequiredCount(cache, "uploadsQueued"),
            Uploading = RequiredCount(cache, "uploadsInProgress"), Errors = RequiredCount(cache, "erroredFiles") };
    }

    internal static bool QueueIsEmpty(IDictionary<string, object> response)
    {
        object queue;
        if (response == null || !response.TryGetValue("queue", out queue) || !(queue is IList))
            throw new InvalidDataException("Mount control did not provide a valid upload queue.");
        return ((IList)queue).Count == 0;
    }

    private static int RequiredCount(IDictionary<string, object> values, string name)
    {
        object value;
        if (!values.TryGetValue(name, out value) || (!(value is int) && !(value is long)))
            throw new InvalidDataException("Mount control omitted a valid " + name + " count.");
        long count = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        if (count < 0 || count > Int32.MaxValue)
            throw new InvalidDataException("Mount control returned an invalid " + name + " count.");
        return (int)count;
    }

    internal static CacheActivitySummary ParseLatestSummary(string tail)
    {
        if (String.IsNullOrEmpty(tail)) return null;
        string latest = null;
        string[] lines = tail.Split('\n');
        // Ignore a final incomplete record while rclone is appending it.
        if (lines[lines.Length - 1].IndexOf("vfs cache: cleaned:", StringComparison.Ordinal) >= 0) return null;
        for (int i = 0; i < lines.Length - 1; i++)
            if (lines[i].IndexOf("vfs cache: cleaned:", StringComparison.Ordinal) >= 0) latest = lines[i].TrimEnd('\r');
        if (latest == null) return null;
        Match match = SummaryPattern.Match(latest);
        DateTime local;
        int used, queued, uploading;
        if (!match.Success || !DateTime.TryParseExact(match.Groups["stamp"].Value, "yyyy/MM/dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out local) ||
            !Int32.TryParse(match.Groups["used"].Value, out used) ||
            !Int32.TryParse(match.Groups["queued"].Value, out queued) ||
            !Int32.TryParse(match.Groups["uploading"].Value, out uploading))
            throw new InvalidDataException("The latest cache activity summary could not be read safely.");
        // Around a DST clock transition we cannot prove the record is fresh.
        if (TimeZoneInfo.Local.IsInvalidTime(local) || TimeZoneInfo.Local.IsAmbiguousTime(local))
            throw new InvalidDataException("The cache activity timestamp is ambiguous; Google Drive has been left mounted.");
        return new CacheActivitySummary { TimestampUtc = TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.Local),
            InUse = used, Queued = queued, Uploading = uploading };
    }

    private string Wait(string detail) { ResetIdle(); return detail; }
    private void ResetIdle() { idleSinceUtc = null; idleTaskRunUtc = null; idleSummaryUtc = null; }
    private void VerifyOffPath()
    {
        if (offVerified) return;
        if (environment.MountPathPresent())
            throw new InvalidOperationException("The task is stopped, but the mount path still exists. Its state is unknown.");
        offVerified = true;
    }
    private static IDictionary<string, object> EmptyInput() { return new Dictionary<string, object>(); }
    private static MountSnapshot Snapshot(string state, string detail, int queued, int uploading)
    {
        return new MountSnapshot { State = state, Detail = detail, UploadsQueued = queued, UploadsInProgress = uploading };
    }
}

internal sealed class WindowsMountEnvironment : IMountEnvironment
{
    private readonly MountSettings settings;
    private Task<bool> pendingPathCheck;
    internal WindowsMountEnvironment(MountSettings configuration) { settings = configuration; }

    public MountTaskInfo ReadTask()
    {
        object service = null, folder = null, task = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder("\\");
            task = ((dynamic)folder).GetTask(settings.TaskName);
            DateTime lastRun = (DateTime)((dynamic)task).LastRunTime;
            return new MountTaskInfo { Enabled = (bool)((dynamic)task).Enabled, State = (int)((dynamic)task).State,
                LastRunUtc = lastRun.Year > 1900 ? DateTime.SpecifyKind(lastRun, DateTimeKind.Local).ToUniversalTime() : DateTime.MinValue };
        }
        catch (COMException ex)
        {
            if (ex.ErrorCode == unchecked((int)0x80070002)) return null;
            throw new InvalidOperationException("The mount task could not be inspected.", ex);
        }
        finally { Release(task); Release(folder); Release(service); }
    }

    public void RunTask()
    {
        object service = null, folder = null, task = null, running = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder("\\");
            task = ((dynamic)folder).GetTask(settings.TaskName);
            if (!(bool)((dynamic)task).Enabled || (int)((dynamic)task).State != 3)
                throw new InvalidOperationException("The mount task is no longer ready to start.");
            running = ((dynamic)task).Run(null);
        }
        catch (COMException ex) { throw new InvalidOperationException("The mount task could not be started.", ex); }
        finally { Release(running); Release(task); Release(folder); Release(service); }
    }

    private static void Release(object value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    public IDictionary<string, object> Call(string route, IDictionary<string, object> input)
    {
        // Never put credentials in command lines, URLs, diagnostics or logs.
        byte[] protectedBytes = null, clearBytes = null;
        string password;
        try
        {
            if (new FileInfo(settings.CredentialPath).Length > 16384)
                throw new InvalidDataException("Invalid credential size.");
            protectedBytes = File.ReadAllBytes(settings.CredentialPath);
            clearBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            password = new UTF8Encoding(false, true).GetString(clearBytes);
            if (password.Length < 32) throw new InvalidDataException("Invalid credential length.");
        }
        catch (Exception ex) { throw new InvalidOperationException("Mount control credentials are unavailable for this Windows user.", ex); }
        finally
        {
            if (protectedBytes != null) Array.Clear(protectedBytes, 0, protectedBytes.Length);
            if (clearBytes != null) Array.Clear(clearBytes, 0, clearBytes.Length);
        }

        var serializer = new JavaScriptSerializer { MaxJsonLength = 262144, RecursionLimit = 32 };
        byte[] body = Encoding.UTF8.GetBytes(serializer.Serialize(input));
        var request = (HttpWebRequest)WebRequest.Create(new Uri(new Uri(settings.Endpoint), route));
        request.Method = "POST";
        request.ContentType = "application/json";
        request.Accept = "application/json";
        request.Proxy = null;
        request.AllowAutoRedirect = false;
        request.KeepAlive = false;
        request.Timeout = 2000;
        request.ReadWriteTimeout = 2000;
        request.ServicePoint.Expect100Continue = false;
        byte[] basic = Encoding.UTF8.GetBytes(settings.Username + ":" + password);
        try { request.Headers[HttpRequestHeader.Authorization] = "Basic " + Convert.ToBase64String(basic); }
        finally { Array.Clear(basic, 0, basic.Length); password = null; }
        request.ContentLength = body.Length;
        try
        {
            using (Stream stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new InvalidDataException("Mount control returned an unexpected HTTP status.");
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    var content = new StringBuilder();
                    char[] chunk = new char[4096];
                    int count;
                    while ((count = reader.Read(chunk, 0, chunk.Length)) != 0)
                    {
                        if (content.Length + count > 262144) throw new InvalidDataException("Mount control response is too large.");
                        content.Append(chunk, 0, count);
                    }
                    var result = serializer.DeserializeObject(content.ToString()) as IDictionary<string, object>;
                    if (result == null) throw new InvalidDataException("Mount control returned an invalid JSON object.");
                    return result;
                }
            }
        }
        catch (WebException ex)
        {
            if (ex.Status == WebExceptionStatus.ConnectFailure && IsConnectionRefused(ex))
                throw new MountEndpointAbsentException();
            var response = ex.Response as HttpWebResponse;
            if (response != null)
            {
                int code = (int)response.StatusCode;
                response.Close();
                if (code == 401 || code == 403) throw new InvalidOperationException("Authentication to mount control failed.");
                throw new InvalidOperationException("Mount control returned HTTP " + code.ToString(CultureInfo.InvariantCulture) + ".");
            }
            throw new InvalidOperationException("Mount control did not respond reliably; its state is unknown.");
        }
    }

    private static bool IsConnectionRefused(Exception error)
    {
        for (Exception current = error; current != null; current = current.InnerException)
        {
            var socket = current as SocketException;
            if (socket != null && socket.SocketErrorCode == SocketError.ConnectionRefused) return true;
        }
        return false;
    }

    public bool MountPathPresent()
    {
        // A root attribute check only: never enumerate or open cloud children.
        // Keep at most one pending operation if a filesystem driver stalls.
        if (pendingPathCheck == null)
            pendingPathCheck = Task.Factory.StartNew(delegate {
                try { File.GetAttributes(settings.MountPath); return true; }
                catch (FileNotFoundException) { return false; }
                catch (DirectoryNotFoundException) { return false; }
            }, System.Threading.CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
        try
        {
            if (!pendingPathCheck.Wait(2000))
                throw new InvalidOperationException("The mount path did not respond; its state is unknown.");
            bool present = pendingPathCheck.Result;
            pendingPathCheck = null;
            return present;
        }
        catch (AggregateException)
        {
            pendingPathCheck = null;
            throw new InvalidOperationException("The mount path could not be checked; its state is unknown.");
        }
    }

    public string ReadLogTail()
    {
        try
        {
            using (var stream = new FileStream(settings.LogPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan))
            {
                const int limit = 32768;
                long start = Math.Max(0, stream.Length - limit);
                stream.Seek(start, SeekOrigin.Begin);
                byte[] bytes = new byte[limit];
                int used = 0, count;
                while (used < limit && (count = stream.Read(bytes, used, limit - used)) > 0) used += count;
                string tail = Encoding.UTF8.GetString(bytes, 0, used);
                if (start > 0)
                {
                    int newline = tail.IndexOf('\n');
                    tail = newline < 0 ? "" : tail.Substring(newline + 1);
                }
                return tail;
            }
        }
        catch (FileNotFoundException) { return ""; }
        catch (DirectoryNotFoundException) { return ""; }
        catch (IOException ex) { throw new InvalidOperationException("The mount activity log could not be read.", ex); }
        catch (UnauthorizedAccessException ex) { throw new InvalidOperationException("The mount activity log is not accessible.", ex); }
    }
}
