using System.Net.Sockets;
using System.Text;

Console.WriteLine("Starting TradingClient...");
string serverIp = "127.0.0.1";
int port = 5000;

using TcpClient client = new TcpClient();

Console.WriteLine("Connecting to server...");
client.Connect(serverIp, port);
Console.WriteLine("Connected to server.");

using NetworkStream stream = client.GetStream();

// Writer: sends lines (requests)
using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

// Reader: reads lines (responses)
using var reader = new StreamReader(stream, new UTF8Encoding(false));


//Allow for continuous commands
while (true)
{
    Console.Write("Enter command (or QUIT): ");
    string? input = Console.ReadLine();

    if (input == null)
        continue;

    input = input.Trim();


    //Disconnect
    if (input.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("Closing connection.");
        break;
    }

    if (input.Length == 0)
        continue;

    // Send exactly one line as the request
    writer.WriteLine(input);

    
   // Read response lines until END (or disconnect)
    while (true)
    {
        string? line = reader.ReadLine();

        if (line == null)
        {
            Console.WriteLine("Server closed the connection.");
            return;
        }

        if (line == "END")
            break;

        Console.WriteLine(line);
    }
}