using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace SlimeVrOta.Gui
{
    public partial class MainWindow : Window
    {
        public FilePickerFileType ZipFileType =
            new("ZIP archive")
            {
                Patterns = ["*.zip"],
                AppleUniformTypeIdentifiers = ["public.zip-archive"],
                MimeTypes = ["application/zip", "x-zip-compressed"],
            };

        private IStorageFile? _firmwareFile;

        public MainWindow()
        {
            InitializeComponent();

            FirmwareDropBox.AddHandler(DragDrop.DropEvent, SelectFirmwareDrop);
            SelectFileButton.Click += SelectFirmwareBrowse;
            FlashButton.Click += FlashFirmware;
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);

            MinWidth = Width + 100;
            MinHeight = Height;
            MaxHeight = Height;

            SizeToContent = SizeToContent.Manual;
        }

        private void SelectFile(IStorageFile file)
        {
            _firmwareFile = file;
            FirmwareFileText.Text = Uri.UnescapeDataString(file.Path.AbsolutePath);

            FlashButton.IsEnabled = true;
            FlashStatusText.Text = "Ready to flash...";
        }

        public void SelectFirmwareDrop(object? sender, DragEventArgs e)
        {
            if (e.Data.Contains(DataFormats.Files))
            {
                var files = e.Data.GetFiles();
                var file =
                    files?.FirstOrDefault(file =>
                        file is IStorageFile
                        && file.Name.EndsWith(".zip", StringComparison.CurrentCultureIgnoreCase)
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
            var files = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions()
                {
                    Title = "Select firmware ZIP...",
                    FileTypeFilter = [ZipFileType],
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

        public void FlashFirmware(object? sender, RoutedEventArgs e)
        {
            if (_firmwareFile == null)
                return;
            Debug.WriteLine("Flashing firmware");
        }
    }
}
