using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using SlimeVrOta;

try
{
    var file = new FileInfo("firmware-part-0.bin");
    if (!file.Exists)
    {
        throw new FileNotFoundException(
            $"Could not find firmware file {file.FullName}.",
            file.FullName
        );
    }

    var port = 6969;
    var endPoint = new IPEndPoint(IPAddress.Any, port);
    using var slimeClient = new UdpClient(port);

    Console.WriteLine("Waiting to receive tracker handshake...");

    var data = slimeClient.Receive(ref endPoint);
    var packetType = BinaryPrimitives.ReadUInt32BigEndian(data);

    // Handshake packet
    if (packetType != 3)
    {
        throw new Exception($"Received a non-handshake packet on {port} from {endPoint}.");
    }
    Console.WriteLine($"Received a handshake packet on {port} from {endPoint}.");

    Console.WriteLine("Press enter to flash the tracker...");
    Console.ReadLine();

    var flashResult = EspOta
        .Serve(
            new IPEndPoint(endPoint.Address, 8266),
            file.Name,
            File.ReadAllBytes(file.FullName),
            "SlimeVR-OTA",
            EspOta.OtaCommands.FLASH
        )
        .Result;

    if (!flashResult)
    {
        throw new Exception($"Failed to flash tracker {endPoint}.");
    }

    Console.WriteLine("Waiting to receive post-flash handshake...");

    data = slimeClient.Receive(ref endPoint);
    packetType = BinaryPrimitives.ReadUInt32BigEndian(data);

    // Handshake packet
    if (packetType != 3)
    {
        throw new Exception($"Received a non-handshake packet on {port} from {endPoint}.");
    }
    Console.WriteLine($"Received a handshake packet on {port} from {endPoint}.");

    Console.WriteLine($"Tracker {endPoint} has been flashed successfully.");
    Console.WriteLine("Press any key to exit...");
    Console.ReadKey();
}
catch (Exception e)
{
    Console.Error.WriteLine(e);
    Console.WriteLine("Press any key to exit...");
    Console.ReadKey();
}
