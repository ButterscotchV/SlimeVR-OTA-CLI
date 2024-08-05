using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace SlimeVrOta
{
    public static class EspOta
    {
        public enum OtaCommands
        {
            FLASH = 0,
            SPIFFS = 100,
            AUTH = 200,
        }

        private static string Md5Hash(ReadOnlySpan<byte> bytes)
        {
            return Convert.ToHexString(MD5.HashData(bytes)).ToLower();
        }

        private static string Md5Hash(string text)
        {
            return Md5Hash(Encoding.UTF8.GetBytes(text));
        }

        public static Task<bool> Serve(
            IPEndPoint remoteEndPoint,
            string fileName,
            byte[] fileData,
            string auth = "",
            OtaCommands command = OtaCommands.FLASH,
            IProgress<(int cur, int max)>? progress = null,
            CancellationToken cancelToken = default
        )
        {
            return Serve(
                remoteEndPoint,
                new IPEndPoint(IPAddress.Any, 0),
                fileName,
                fileData,
                auth,
                command,
                progress,
                cancelToken
            );
        }

        public static Task<bool> Serve(
            IPEndPoint remoteEndPoint,
            IPEndPoint localEndPoint,
            string fileName,
            byte[] fileData,
            string auth = "",
            OtaCommands command = OtaCommands.FLASH,
            IProgress<(int cur, int max)>? progress = null,
            CancellationToken cancelToken = default
        )
        {
            return Serve(
                remoteEndPoint,
                localEndPoint,
                fileName,
                fileData,
                TimeSpan.FromSeconds(10),
                auth,
                command,
                progress,
                cancelToken
            );
        }

        public static Task<bool> Serve(
            IPEndPoint remoteEndPoint,
            string fileName,
            byte[] fileData,
            TimeSpan timeout,
            string auth = "",
            OtaCommands command = OtaCommands.FLASH,
            IProgress<(int cur, int max)>? progress = null,
            CancellationToken cancelToken = default
        )
        {
            return Serve(
                remoteEndPoint,
                new IPEndPoint(IPAddress.Any, 0),
                fileName,
                fileData,
                timeout,
                auth,
                command,
                progress,
                cancelToken
            );
        }

        public static async Task<bool> Serve(
            IPEndPoint remoteEndPoint,
            IPEndPoint localEndPoint,
            string fileName,
            byte[] fileData,
            TimeSpan timeout,
            string auth = "",
            OtaCommands command = OtaCommands.FLASH,
            IProgress<(int cur, int max)>? progress = null,
            CancellationToken cancelToken = default
        )
        {
            Console.WriteLine("Starting OTA...");

            var timeoutMs = (int)timeout.TotalMilliseconds;
            using var listener = new TcpListener(localEndPoint);
            listener.Server.NoDelay = true;
            listener.Server.SendTimeout = timeoutMs;
            listener.Server.ReceiveTimeout = timeoutMs;
            listener.Start();
            var listenerEndPoint = (IPEndPoint)listener.LocalEndpoint;

            var contentSize = fileData.Length;
            var fileMd5 = Md5Hash(fileData);

            using var initClient = new UdpClient();
            initClient.Client.SendTimeout = timeoutMs;
            initClient.Client.ReceiveTimeout = timeoutMs;
            initClient.Connect(remoteEndPoint);

            await initClient.SendAsync(
                Encoding.UTF8.GetBytes(
                    $"{(int)command} {listenerEndPoint.Port} {contentSize} {fileMd5}\n"
                ),
                cancelToken
            );

            UdpReceiveResult initResponse;
            try
            {
                using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancelToken
                );
                receiveTimeout.CancelAfter(timeout);
                initResponse = await initClient.ReceiveAsync(receiveTimeout.Token);
            }
            catch (Exception)
            {
                Console.WriteLine("OTA request failed, no response");
                return false;
            }

            var initResponseText = Encoding.UTF8.GetString(initResponse.Buffer);
            if (initResponseText != "OK")
            {
                if (initResponseText.StartsWith("AUTH"))
                {
                    var nonce = initResponseText.Split()[1];
                    var cnonceText = $"{fileName}{contentSize}{fileMd5}{remoteEndPoint.Address}";
                    var cnonce = Md5Hash(cnonceText);
                    var resultText = $"{Md5Hash(auth)}:{nonce}:{cnonce}";
                    var result = Md5Hash(resultText);

                    await initClient.SendAsync(
                        Encoding.UTF8.GetBytes($"{(int)OtaCommands.AUTH} {cnonce} {result}\n"),
                        cancelToken
                    );

                    UdpReceiveResult authResponse;
                    try
                    {
                        using var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                            cancelToken
                        );
                        authTimeout.CancelAfter(timeout);
                        authResponse = await initClient.ReceiveAsync(authTimeout.Token);
                    }
                    catch (Exception)
                    {
                        Console.WriteLine("Auth failed, no response");
                        return false;
                    }

                    var authResponseText = Encoding.UTF8.GetString(authResponse.Buffer);
                    if (authResponseText != "OK")
                    {
                        Console.WriteLine($"Auth failed, bad response: {authResponseText}");
                        return false;
                    }
                }
                else
                {
                    Console.WriteLine($"Bad response: {initResponseText}");
                    return false;
                }
            }

            Console.WriteLine("Waiting for device ...");
            TcpClient device;
            try
            {
                using var acceptTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancelToken
                );
                acceptTimeout.CancelAfter(timeout);
                device = await listener.AcceptTcpClientAsync(acceptTimeout.Token);
            }
            catch (Exception)
            {
                Console.WriteLine("No response from device");
                return false;
            }

            if (!device.Connected)
            {
                Console.WriteLine("Device did not connect");
                return false;
            }

            Console.WriteLine("Connected to device, sending firmware");
            device.ReceiveTimeout = timeoutMs;
            device.SendTimeout = timeoutMs;

            using var fileStream = new MemoryStream(fileData);
            var bytesWritten = 0;
            var buffer = new byte[1024];
            var chunkSize = 0;
            var response = "";
            while ((chunkSize = await fileStream.ReadAsync(buffer, cancelToken)) > 0)
            {
                bytesWritten += chunkSize;

                await device.Client.SendAsync(buffer.AsMemory(0, chunkSize), cancelToken);
                Console.WriteLine(
                    $"Written {bytesWritten} out of {contentSize} ({bytesWritten / (float)contentSize:0.0%})"
                );
                progress?.Report((bytesWritten, contentSize));

                try
                {
                    using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                        cancelToken
                    );
                    responseTimeout.CancelAfter(timeout);
                    var responseSize = await device.Client.ReceiveAsync(
                        buffer.AsMemory(0, 10),
                        responseTimeout.Token
                    );
                    response = Encoding.UTF8.GetString(buffer.AsSpan(0, responseSize));
                }
                catch (Exception)
                {
                    Console.WriteLine("Lost connection while writing firmware");
                    return false;
                }
            }

            if (response.Contains("OK"))
            {
                Console.WriteLine("Success");
                return true;
            }

            Console.WriteLine("Waiting for response...");
            device.ReceiveTimeout = 60000;
            for (var i = 0; i < 5; i++)
            {
                try
                {
                    using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                        cancelToken
                    );
                    responseTimeout.CancelAfter(timeout);
                    var responseSize = await device.Client.ReceiveAsync(
                        buffer.AsMemory(0, 32),
                        responseTimeout.Token
                    );
                    response = Encoding.UTF8.GetString(buffer.AsSpan(0, responseSize));
                }
                catch (Exception)
                {
                    Console.WriteLine("Lost connection while waiting for response");
                    return false;
                }
                Console.WriteLine($"Result: {response}");

                if (response.Contains("OK"))
                {
                    Console.WriteLine("Success");
                    return true;
                }
            }

            Console.WriteLine("Error response from device");
            return false;
        }
    }
}
