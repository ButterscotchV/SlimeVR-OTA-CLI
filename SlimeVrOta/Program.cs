using System.Net;
using SlimeVrOta;

var file = new FileInfo("C:/Users/Butterscotch/Downloads/ESPOTA/firmware-part-0.bin");

EspOta
    .Serve(
        new IPEndPoint(new IPAddress([192, 168, 4, 112]), 8266),
        file.Name,
        File.ReadAllBytes(file.FullName),
        "SlimeVR-OTA",
        EspOta.OtaCommands.FLASH
    )
    .Wait();
