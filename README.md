# wpf-demo

장비를 검색해 대여·반납하고 이력을 확인하는 Windows 앱입니다. WPF 클라이언트는 로컬 .NET 10 API와 통신합니다. 데이터는 모두 가상입니다.

## Windows에서 실행

이 변경을 포함한 [Windows Actions 실행](https://github.com/GyutaeLee/wpf-demo/actions/workflows/windows.yml)에서 `wpf-demo-windows` 산출물을 받습니다. 압축을 풀고 안의 `wpf-demo-windows-demo.zip`도 풉니다.

1. `api\WpfDemo.Api.exe`를 실행합니다.
2. 다른 터미널에서 `client\WpfDemo.exe`를 실행합니다.

WPF 앱에는 .NET Framework 4.8 이상이 필요합니다. 자동 시나리오 실행은 [테스트 안내](docs/testing.md)를 참고하세요.

## 구현

화면은 XAML 바인딩과 직접 작성한 MVVM으로 만들었습니다. `StatusBadge`는 목록과 상세에서 함께 쓰는 WPF Custom Control입니다. API는 SQLite에 장비와 대여 이력을 저장합니다.

```sh
dotnet test tests/WpfDemo.Tests/WpfDemo.Tests.csproj -c Release
```

요청 처리 시나리오는 [docs/scenarios.md](docs/scenarios.md)에 정리했습니다.
