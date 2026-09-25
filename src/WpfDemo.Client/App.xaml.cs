using System.Windows;

namespace WpfDemo
{
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var viewModel = new WorklistViewModel(new HttpWorkItemApi());
            var window = new MainWindow { DataContext = viewModel };
            MainWindow = window;
            window.Show();
            await viewModel.LoadAsync();
        }
    }
}
