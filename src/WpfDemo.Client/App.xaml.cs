using System.Windows;
using System;
using System.IO;
using System.Windows.Media;

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

            ReadCacheStore cache = null;
            try { cache = new ReadCacheStore(Path.Combine(dataDirectory, "read-cache.db")); }
            catch (Exception)
            {
                MessageBox.Show("로컬 조회 캐시를 열지 못했습니다. 온라인 사용은 가능하지만 연결이 끊기면 이전 조회 결과만 남습니다.",
                    "wpf-demo", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            var label = Environment.GetEnvironmentVariable("WPFDEMO_CLIENT_LABEL") ?? "";
            var diagnosticsOutputDirectory = Environment.GetEnvironmentVariable("WPFDEMO_DIAGNOSTICS_OUTPUT_DIR");
            if (string.IsNullOrWhiteSpace(diagnosticsOutputDirectory))
                diagnosticsOutputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            ClientRequestLog requestLog = null;
            try { requestLog = new ClientRequestLog(Path.Combine(dataDirectory, "diagnostics")); }
            catch (Exception)
            {
                MessageBox.Show("클라이언트 진단 로그를 만들지 못했습니다. 진단 내보내기는 사용할 수 없지만 그 외 기능은 계속 동작합니다.",
                    "wpf-demo", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            MainWindow window = null;
            var viewModel = new LendingViewModel(new HttpLendingApi(apiAddress, requestLog), store,
                apiAddress, string.Equals(label, "B", StringComparison.OrdinalIgnoreCase) ? "B" : "A", cache,
                requestLog, () => WindowDpi(window), diagnosticsOutputDirectory);
            window = new MainWindow { DataContext = viewModel, Title = string.IsNullOrWhiteSpace(label) ? "wpf-demo — 장비 대여" : "wpf-demo — 클라이언트 " + label };
            window.Closed += (sender, args) => { store.Dispose(); cache?.Dispose(); };
            MainWindow = window;
            window.Show();
            await viewModel.InitializeAsync();
        }

        private static double WindowDpi(MainWindow window)
        {
            return window == null ? 96.0 : VisualTreeHelper.GetDpi(window).PixelsPerInchX;
        }
    }
}
