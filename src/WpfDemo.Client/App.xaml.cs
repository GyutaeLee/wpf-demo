using System.Windows;
using System;
using System.IO;

namespace WpfDemo
{
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var apiAddress = Environment.GetEnvironmentVariable("WPFDEMO_API_URL");
            if (string.IsNullOrWhiteSpace(apiAddress)) apiAddress = "http://127.0.0.1:5187";
            var dataDirectory = Environment.GetEnvironmentVariable("WPFDEMO_CLIENT_DATA_DIR");
            if (string.IsNullOrWhiteSpace(dataDirectory))
                dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wpf-demo", "client");
            PendingOperationStore store;
            try { store = new PendingOperationStore(dataDirectory); }
            catch (IOException)
            {
                MessageBox.Show("이 클라이언트 저장 폴더를 이미 사용 중입니다. 다른 실행을 닫고 다시 시작해 주세요.",
                    "wpf-demo", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            var label = Environment.GetEnvironmentVariable("WPFDEMO_CLIENT_LABEL") ?? "";
            var viewModel = new LendingViewModel(new HttpLendingApi(apiAddress), store,
                apiAddress, string.Equals(label, "B", StringComparison.OrdinalIgnoreCase) ? "B" : "A");
            var window = new MainWindow { DataContext = viewModel, Title = string.IsNullOrWhiteSpace(label) ? "wpf-demo — 장비 대여" : "wpf-demo — 클라이언트 " + label };
            window.Closed += (sender, args) => store.Dispose();
            MainWindow = window;
            window.Show();
            await viewModel.InitializeAsync();
        }
    }
}
