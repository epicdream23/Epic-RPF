using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace App.UI;

/// <summary>
/// One-running-instance coordination via a named pipe. When the app is launched with
/// a file (e.g. double-clicking a .ytd associated to it), it tries to hand the path to
/// an already-running instance — which opens it in a new tab — and exits. If no instance
/// is running, it becomes the primary, serving the pipe so future launches forward here.
/// </summary>
internal static class SingleInstance
{
    // Per-user pipe so two accounts don't collide.
    private static string PipeName => "EpicRpf.OpenFile." + Environment.UserName;

    /// <summary>Try to hand <paramref name="path"/> to a running instance. True = delivered (this process should exit).</summary>
    public static bool TrySendToExisting(string path)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(400);                       // short timeout: no server -> we're primary
            byte[] data = Encoding.UTF8.GetBytes(path);
            client.Write(data, 0, data.Length);
            client.Flush();
            return true;
        }
        catch { return false; }                        // no server listening
    }

    /// <summary>Serve the pipe; each delivered path is passed to <paramref name="onPath"/>.</summary>
    public static void StartServer(Action<string> onPath)
    {
        _ = Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.None);
                    server.WaitForConnection();
                    using var ms = new MemoryStream();
                    server.CopyTo(ms);
                    string path = Encoding.UTF8.GetString(ms.ToArray()).Trim();
                    if (path.Length > 0) onPath(path);
                }
                catch { Thread.Sleep(200); }           // keep the server alive through transient errors
            }
        });
    }
}
