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
    var udpBuffer = new byte[65535];
    using var slimeSocket = new Socket(
        AddressFamily.InterNetwork,
        SocketType.Dgram,
        ProtocolType.Udp
    );
    slimeSocket.Bind(new IPEndPoint(IPAddress.Any, port));
    var endPoint = new IPEndPoint(IPAddress.Any, port);

    async Task WaitForHandshake()
    {
        var data = await slimeSocket.ReceiveFromAsync(udpBuffer, endPoint);
        if (data.ReceivedBytes < 4 || data.RemoteEndPoint is not IPEndPoint receivedEndPoint)
        {
            throw new Exception(
                $"Received an invalid SlimeVR packet on port {port} from {data.RemoteEndPoint}."
            );
        }

        var packetType = BinaryPrimitives.ReadUInt32BigEndian(udpBuffer);
        // 3 is a handshake packet
        if (packetType != 3)
        {
            throw new Exception(
                $"Received a non-handshake packet on port {port} from {data.RemoteEndPoint}."
            );
        }

        endPoint = receivedEndPoint;
    }

    Console.WriteLine("Waiting to receive tracker handshake...");
    await WaitForHandshake();
    Console.WriteLine($"Received a handshake packet on port {port} from {endPoint}.");

    Console.WriteLine("Press enter to flash the tracker...");
    Console.ReadLine();

    try
    {
        await EspOta.Serve(
            new IPEndPoint(endPoint.Address, 8266),
            file.Name,
            await File.ReadAllBytesAsync(file.FullName),
            "SlimeVR-OTA",
            EspOta.OtaCommands.FLASH
        );
    }
    catch (Exception ex)
    {
        throw new OtaException($"Failed to flash tracker {endPoint}.", ex);
    }

    Console.WriteLine("Waiting to receive post-flash handshake...");
    await WaitForHandshake();
    Console.WriteLine($"Received a handshake packet on port {port} from {endPoint}.");

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
