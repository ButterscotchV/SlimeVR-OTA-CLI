using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace SlimeVrOta.Gui
{
    public partial class MainWindow : Window
    {
        public enum FlashState
        {
            Connecting,
            Ready,
            Flashing,
            Waiting,
            Done,
            Error
        }

        public static readonly FilePickerFileType ZipFileType =
            new("ZIP archive")
            {
                Patterns = ["*.zip"],
                AppleUniformTypeIdentifiers = ["public.zip-archive"],
                MimeTypes = ["application/zip", "x-zip-compressed"],
            };

        public static readonly FilePickerFileType BinFileType =
            new("Firmware binary")
            {
                Patterns = ["*.bin"],
                AppleUniformTypeIdentifiers = ["public.data"],
                MimeTypes = ["application/octet-stream"],
            };

        public static readonly FilePickerFileType FirmwareFileType =
            new("Firmware ZIP or binary")
            {
                Patterns = [.. ZipFileType.Patterns, .. BinFileType.Patterns],
                AppleUniformTypeIdentifiers =
                [
                    .. ZipFileType.AppleUniformTypeIdentifiers,
                    .. BinFileType.AppleUniformTypeIdentifiers
                ],
                MimeTypes = [.. ZipFileType.MimeTypes, .. BinFileType.MimeTypes],
            };

        private IStorageFile? _firmwareFile;

        private static readonly int _port = 6969;
        private readonly byte[] _udpBuffer = new byte[65535];
        private static readonly IPEndPoint _localEndPoint = new(IPAddress.Any, _port);

        private bool _runDiscoveryOnClose = false;
        private Socket? _socket;
        private IPEndPoint _endPoint = new(IPAddress.Any, _port);

        private FlashState _currentState = FlashState.Connecting;
        public FlashState CurrentState
        {
            get => _currentState;
            set
            {
                _currentState = value;
                UpdateFileStatus();
                UpdateTrackerStatus();
                UpdateFlashStatus();
            }
        }

        public bool IsReady =>
            CurrentState == FlashState.Ready
            || CurrentState == FlashState.Done
            || CurrentState == FlashState.Error;
        public bool CanAcceptFile => CurrentState == FlashState.Connecting || IsReady;

        public MainWindow()
        {
            InitializeComponent();

            CloseErrorButton.Click += CloseError;
            FirmwareDropBox.AddHandler(DragDrop.DropEvent, SelectFirmwareDrop);
            SelectFileButton.Click += SelectFirmwareBrowse;
            RemoveTrackerButton.Click += RemoveTracker;
            FlashButton.Click += FlashFirmware;
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);

            MinWidth = Width + 100;
            MinHeight = Height;
            MaxHeight = Height;
            SizeToContent = SizeToContent.Manual;
            Title = $"SlimeVR OTA Tool v{Constants.Version}";

            _ = ReceiveHandshake();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            _socket?.Dispose();
            _socket = null;
        }

        private void ShowError(string text)
        {
            ErrorText.Text = text;
            ErrorPopup.IsOpen = true;
        }

        public void CloseError(object? sender, RoutedEventArgs e)
        {
            ErrorText.Text = "Unknown error.";
            ErrorPopup.IsOpen = false;

            if (_runDiscoveryOnClose && CurrentState == FlashState.Connecting)
            {
                _ = ReceiveHandshake(5000);
            }
            _runDiscoveryOnClose = false;
        }

        private void UpdateFileStatus()
        {
            if (CanAcceptFile)
            {
                SelectFileButton.IsEnabled = true;
                DragDrop.SetAllowDrop(FirmwareDropBox, true);
            }
            else
            {
                SelectFileButton.IsEnabled = false;
                DragDrop.SetAllowDrop(FirmwareDropBox, false);
            }
        }

        private void SelectFile(IStorageFile file)
        {
            if (!CanAcceptFile)
                return;

            _firmwareFile = file;

            var path = Uri.UnescapeDataString(file.Path.AbsolutePath);
            FirmwareFileText.Text = path;
            ToolTip.SetTip(FirmwareFileText, path);

            UpdateFlashStatus();
        }

        public void SelectFirmwareDrop(object? sender, DragEventArgs e)
        {
            if (!CanAcceptFile)
                return;

            if (e.Data.Contains(DataFormats.Files))
            {
                var files = e.Data.GetFiles();
                var file =
                    files?.FirstOrDefault(file =>
                        file is IStorageFile
                        && (
                            file.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                            || file.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                        )
                    ) as IStorageFile;
                if (file != null)
                {
                    Debug.WriteLine("Dropped file(s)");
                    SelectFile(file);
                }
            }
        }

        public async void SelectFirmwareBrowse(object? sender, RoutedEventArgs e)
        {
            if (!CanAcceptFile)
                return;

            var files = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions()
                {
                    Title = "Select firmware file...",
                    FileTypeFilter = [FirmwareFileType],
                    AllowMultiple = false,
                }
            );
            var file = files?.Count > 0 ? files[0] : null;

            if (file != null)
            {
                Debug.WriteLine("Selected file");
                SelectFile(file);
            }
        }

        private void UpdateTrackerStatus()
        {
            if (CurrentState == FlashState.Connecting)
            {
                RemoveTrackerButton.IsEnabled = false;
                TrackerStatusText.Text = "Waiting for tracker to connect...";
            }
            else
            {
                RemoveTrackerButton.IsEnabled = IsReady;
                TrackerStatusText.Text = _endPoint.ToString();
            }
        }

        public async Task ReceiveHandshake(int delay = 0, CancellationToken cancelToken = default)
        {
            if (delay > 0)
            {
                try
                {
                    await Task.Delay(delay, cancelToken);
                }
                catch
                {
                    if (cancelToken.IsCancellationRequested)
                        return;
                }
            }

            if (_socket == null || _socket?.IsBound != true)
            {
                try
                {
                    _socket?.Dispose();
                    _socket = new Socket(
                        AddressFamily.InterNetwork,
                        SocketType.Dgram,
                        ProtocolType.Udp
                    );
                    _socket.Bind(_localEndPoint);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                    _runDiscoveryOnClose = true;
                    ShowError(
                        $"Error while binding socket, make sure SlimeVR isn't running and port {_port} is not in use!\n\n{ex}"
                    );
                    return;
                }
            }

            while (!cancelToken.IsCancellationRequested && _socket.IsBound)
            {
                try
                {
                    // Clear socket buffer
                    while (!cancelToken.IsCancellationRequested && _socket.Available > 0)
                    {
                        await _socket.ReceiveFromAsync(_udpBuffer, _endPoint, cancelToken);
                    }

                    var data = await _socket.ReceiveFromAsync(_udpBuffer, _endPoint, cancelToken);
                    if (
                        data.ReceivedBytes < 4
                        || data.RemoteEndPoint is not IPEndPoint remoteEndpoint
                    )
                    {
                        throw new Exception(
                            $"Received an invalid SlimeVR packet on port {_port} from {data.RemoteEndPoint}."
                        );
                    }

                    var packetType = BinaryPrimitives.ReadUInt32BigEndian(_udpBuffer);
                    // 3 is a handshake packet
                    if (packetType != 3)
                    {
                        throw new Exception(
                            $"Received a non-handshake packet on port {_port} from {data.RemoteEndPoint}."
                        );
                    }

                    if (CurrentState == FlashState.Connecting)
                    {
                        _endPoint = remoteEndpoint;
                        CurrentState = FlashState.Ready;
                    }

                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }
            }
        }

        public void RemoveTracker(object? sender, RoutedEventArgs e)
        {
            if (!IsReady)
                return;

            _socket?.Dispose();
            _socket = null;
            _endPoint = new(IPAddress.Any, _port);
            CurrentState = FlashState.Connecting;

            _ = ReceiveHandshake();
        }

        private void UpdateFlashStatus()
        {
            if (_firmwareFile != null && IsReady)
            {
                FlashButton.IsEnabled = true;
                switch (CurrentState)
                {
                    case FlashState.Ready:
                        FlashStatusText.Text = "Ready to flash...";
                        FlashProgress.Value = 0.0;
                        break;
                    case FlashState.Done:
                        FlashStatusText.Text =
                            "Successfully flashed tracker! Test before turning off!";
                        FlashProgress.Value = 100.0;
                        break;
                    case FlashState.Error:
                        FlashStatusText.Text = "Failed to flash tracker.";
                        break;
                }
            }
            else
            {
                FlashButton.IsEnabled = false;

                switch (CurrentState)
                {
                    case FlashState.Connecting:
                        FlashStatusText.Text = "Idle...";
                        FlashProgress.Value = 0.0;
                        break;
                    case FlashState.Flashing:
                        FlashStatusText.Text = "Flashing tracker... DO NOT TURN OFF!";
                        break;
                    case FlashState.Waiting:
                        FlashStatusText.Text = "Waiting for tracker... DO NOT TURN OFF!";
                        break;
                }
            }
        }

        public async void FlashFirmware(object? sender, RoutedEventArgs e)
        {
            if (_firmwareFile == null || !IsReady)
                return;

            Debug.WriteLine("Flashing firmware");
            CurrentState = FlashState.Flashing;
            FlashProgress.Value = 0.0;

            try
            {
                using var fileStream = await _firmwareFile.OpenReadAsync();
                var fileName = _firmwareFile.Name;
                byte[] fileBytes;

                // Handle ZIP
                if (_firmwareFile.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    using var zipFile = new ZipArchive(fileStream);
                    var file =
                        zipFile.GetEntry("firmware-part-0.bin")
                        ?? throw new Exception(
                            "Could not retrieve firmware binary \"firmware-part-0.bin\" from ZIP archive."
                        );
                    fileName = file.Name;

                    using var zipFileStream = file.Open();
                    using var memoryStream = new MemoryStream((int)file.Length);
                    await zipFileStream.CopyToAsync(memoryStream);
                    fileBytes = memoryStream.ToArray();
                }
                else if (_firmwareFile.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                {
                    var fileSize =
                        (await _firmwareFile.GetBasicPropertiesAsync()).Size ?? 1048576UL;

                    using var memoryStream = new MemoryStream((int)fileSize);
                    await fileStream.CopyToAsync(memoryStream);
                    fileBytes = memoryStream.ToArray();
                }
                else
                {
                    throw new Exception("Unexpected firmware file type.");
                }

                Debug.WriteLine($"Loaded file \"{fileName}\" ({fileBytes.Length} bytes)");

                var progress = new Progress<(int cur, int max)>(val =>
                {
                    FlashProgress.Value = (val.cur / (double)val.max) * 50.0;
                });
                await new EspOta().Serve(
                    new IPEndPoint(_endPoint.Address, 8266),
                    new IPEndPoint(IPAddress.Any, 0),
                    fileName,
                    fileBytes,
                    "SlimeVR-OTA",
                    EspOta.OtaCommands.FLASH,
                    progress
                );

                Debug.WriteLine("Waiting for tracker response");
                CurrentState = FlashState.Waiting;

                using var cancelSrc = new CancellationTokenSource();
                var handshake = ReceiveHandshake(cancelToken: cancelSrc.Token);
                // 45 second timeout, it should not take that long
                cancelSrc.CancelAfter(45000);

                using var timerCancel = new CancellationTokenSource();
                try
                {
                    handshake.GetAwaiter().OnCompleted(timerCancel.Cancel);
                    while (
                        !timerCancel.IsCancellationRequested
                        && !handshake.IsCompleted
                        && FlashProgress.Value < 95.0
                    )
                    {
                        await Task.Delay(300, timerCancel.Token);
                        FlashProgress.Value += 1.0;
                    }
                }
                catch
                {
                    // Ignore, this is just to wait for the handshake to finish
                }

                await handshake;

                Debug.WriteLine("Firmware flashed successfully");
                FlashProgress.Value = 100.0;
                CurrentState = FlashState.Done;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                ShowError(ex.ToString());
                CurrentState = FlashState.Error;
            }
        }
    }
}
