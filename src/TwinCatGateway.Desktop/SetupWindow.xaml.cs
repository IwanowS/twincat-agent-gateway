using System.Windows;

namespace TwinCatGateway.Desktop;

public partial class SetupWindow : Window
{
    public SetupWindow()
    {
        InitializeComponent();
        VersionText.Text = GatewayProductVersion.DisplayText;
        InstructionsText.Text = SetupInstructionsProvider.Read();
    }

    private void CloseButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        Close();
    }
}
