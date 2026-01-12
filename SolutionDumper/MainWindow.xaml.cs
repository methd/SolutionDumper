using SolutionDumper.ViewModels;
using System.Windows;

namespace SolutionDumper
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