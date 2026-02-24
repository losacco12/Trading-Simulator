using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

Console.WriteLine("Starting TradingClient...");
string serverIp = "127.0.0.1";
int port = 5000;

using var client = new TcpClient();

Console.WriteLine("Connecting to server...");
client.Connect(serverIp, port);
Console.WriteLine("Connected to server.");

using NetworkStream stream = client.GetStream();
using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
using var reader = new StreamReader(stream, new UTF8Encoding(false));

var cts = new CancellationTokenSource();

// Channel for non-MD response lines (all lines except "MD ...")
var respChan = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
{
    SingleReader = true,
    SingleWriter = true
});

// keep console output from interleaving weirdly
object consoleLock = new object();


// Start ONE reader loop for the entire connection
var readerTask = Task.Run(async () =>
{
    try
    {
        while (!cts.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line == null)
            {
                // Server disconnected
                break;
            }

            long lastSeq = 0;

            if (line.StartsWith("MD "))
            {
                var parts = line.Split(' ', 4); // MD seq ts rest...
                if (parts.Length >= 4 && long.TryParse(parts[1], out var seq))
                {
                    if (lastSeq != 0 && seq != lastSeq + 1)
                        Console.WriteLine($"*** MD GAP: last={lastSeq} now={seq} ***");

                    lastSeq = seq;
                }

                Console.WriteLine(line);
                continue;
            }

            else
            {
                // Push normal lines to response channel
                await respChan.Writer.WriteAsync(line, cts.Token).ConfigureAwait(false);
            }
        }
    }
    catch
    {
        // swallow — main loop will notice closure
    }
    finally
    {
        respChan.Writer.TryComplete();
    }
});

// Foreground command loop
try
{
    while (true)
    {
        lock (consoleLock)
        {
            Console.Write("Enter command (or QUIT): ");
        }

        string? input = Console.ReadLine();
        if (input == null) continue;

        input = input.Trim();

        if (input.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
        {
            lock (consoleLock) Console.WriteLine("Closing connection.");
            break;
        }

        if (input.Length == 0) continue;

        // Send exactly one command line
        writer.WriteLine(input);

        // Read response lines until END (from the channel)
        while (true)
        {
            string? line;
            try
            {
                line = await respChan.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
            }
            catch
            {
                lock (consoleLock) Console.WriteLine("Disconnected.");
                return;
            }

            if (line == "END") break;

            lock (consoleLock)
            {
                Console.WriteLine(line);
            }
        }
    }
}
finally
{
    cts.Cancel();
    try { await readerTask.ConfigureAwait(false); } catch { /* ignore */ }
}
