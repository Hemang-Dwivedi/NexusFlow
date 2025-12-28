using Avalonia.Controls;
using NexusFlow.UI.ViewModels;
namespace NexusFlow.App.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
        }
    }
}