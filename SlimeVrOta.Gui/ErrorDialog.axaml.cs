using Avalonia.Controls;

namespace SlimeVrOta.Gui;

public partial class ErrorDialog : Window
{
    public ErrorDialog()
    {
        InitializeComponent();

        CloseErrorButton.Click += (_, _) =>
        {
            Close();
        };
    }
}
