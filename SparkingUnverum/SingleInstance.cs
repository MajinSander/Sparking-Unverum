using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace SparkingUnverum
{
    // Keeps a single Sparking Unverum open. Later launches (for example from a 1-click install button)
    // hand their arguments to the running window through a named pipe and exit
    public static class SingleInstance
    {
        private const string Name = "SparkingUnverum.SingleInstance";
        public const string ActivateMessage = "--activate";
        private static Mutex mutex;

        // wait is used when restarting, so the new process waits for the old one to close
        public static bool TryAcquire(bool wait = false)
        {
            mutex = new Mutex(false, Name);
            try
            {
                return mutex.WaitOne(wait ? 15000 : 0);
            }
            catch (AbandonedMutexException)
            {
                // The previous instance closed without releasing it
                return true;
            }
        }

        public static bool Send(string message)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", Name, PipeDirection.Out);
                client.Connect(3000);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(message);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void Listen(Action<string> onMessage)
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                        await server.WaitForConnectionAsync();
                        using var reader = new StreamReader(server);
                        var message = await reader.ReadLineAsync();
                        if (!string.IsNullOrEmpty(message))
                            onMessage(message);
                    }
                    catch
                    {
                        await Task.Delay(500);
                    }
                }
            });
        }
    }
}
