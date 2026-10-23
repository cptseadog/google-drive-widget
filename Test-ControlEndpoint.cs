using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Real loopback regression checks with a synthetic DPAPI credential. No Drive,
// scheduled-task, user credential, or mount operations are performed.
static class TestControlEndpoint
{
    static int Main()
    {
        string directory = Path.Combine(Path.GetTempPath(), "DriveWidgetEndpointTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string credential = Path.Combine(directory, "test-credential.bin");
        try
        {
            File.WriteAllBytes(credential, ProtectedData.Protect(Encoding.UTF8.GetBytes(new string('t', 48)), null, DataProtectionScope.CurrentUser));
            var settings = new MountSettings { CredentialPath = credential, Username = "local-test" };
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            settings.Endpoint = "http://127.0.0.1:" + port + "/";
            var environment = new WindowsMountEnvironment(settings);
            for (int i = 0; i < 5; i++)
            {
                var elapsed = Stopwatch.StartNew();
                try { environment.Call("vfs/stats", new Dictionary<string, object>()); throw new Exception("Closed endpoint was accepted."); }
                catch (MountEndpointAbsentException) { }
                if (elapsed.ElapsedMilliseconds > 1000) throw new Exception("Closed-port detection waited for the HTTP timeout.");
            }
            Console.WriteLine("PASS: five closed-port checks report absent without waiting for HTTP timeouts.");
            CheckListeningServer(settings, false);
            CheckListeningServer(settings, true);
            return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL: " + error.Message); return 1; }
        finally { File.Delete(credential); Directory.Delete(directory); }
    }
    static void CheckListeningServer(MountSettings settings, bool unauthorized)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        var finish = new ManualResetEventSlim(false);
        listener.Start();
        settings.Endpoint = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
        Task server = Task.Run(delegate {
            using (TcpClient client = listener.AcceptTcpClient())
            {
                if (unauthorized)
                {
                    byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    NetworkStream stream = client.GetStream();
                    stream.ReadTimeout = 5000;
                    byte[] input = new byte[4096];
                    stream.Read(input, 0, input.Length);
                    stream.Write(response, 0, response.Length);
                }
                else finish.Wait(6000);
            }
        });
        try
        {
            try
            {
                new WindowsMountEnvironment(settings).Call("vfs/stats", new Dictionary<string, object>());
                throw new Exception("Expected the listening endpoint to fail.");
            }
            catch (MountEndpointAbsentException) { throw new Exception("A listening endpoint was incorrectly classified as absent."); }
            catch (InvalidOperationException error)
            {
                if (unauthorized && !error.Message.Contains("Authentication")) throw;
                if (!unauthorized && !error.Message.Contains("unknown")) throw;
            }
            Console.WriteLine(unauthorized ? "PASS: an HTTP 401 remains an authentication error." : "PASS: a listening but stalled endpoint remains unknown.");
        }
        finally { finish.Set(); listener.Stop(); server.Wait(7000); finish.Dispose(); }
    }
}