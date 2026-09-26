# wpf-demo

.NET Framework 4.8 WPF와 .NET 10 로컬 HTTP API로 만든 업무 목록 앱입니다. 목록을 검색하거나 상태로 거르고 선택한 항목의 상태와 메모를 수정해 저장합니다. 모든 데이터는 가상입니다.

![업무 목록과 상세 편집 화면](docs/screenshots/saved.png)

## 구현

- XAML 바인딩과 MVVM으로 검색·선택·입력 검증·저장 상태를 처리합니다. 비동기 요청 중에는 로딩과 저장 상태를 표시합니다.
- 저장 응답을 기다리는 동안 화면 이동과 중복 저장을 막습니다. 저장 실패 시 입력을 유지한 상태에서 재시도할 수 있습니다.
- `StatusBadge`는 dependency property와 `ControlTemplate`을 사용하는 Custom Control입니다. 목록과 상세 화면에 재사용했습니다. 외부 UI 라이브러리는 사용하지 않았습니다.

API는 가상 항목 5개를 메모리에 보관하고 GET·PUT을 제공합니다. 서버를 재시작하면 수정 내용은 초기화됩니다. 화면 상태와 테스트를 구성한 이유는 [시나리오 기록](docs/scenarios.md)에 적었습니다.

## 실행

Windows에서 소스를 빌드하려면 Visual Studio 2026의 .NET 데스크톱 개발 워크로드, .NET Framework 4.8 타기팅 팩과 .NET 10 SDK를 준비합니다.

```powershell
dotnet run --project src/WpfDemo.Api/WpfDemo.Api.csproj
```

API를 실행한 뒤 `wpf-demo.sln`을 열고 `WpfDemo.Client`를 시작 프로젝트로 실행합니다. API 주소는 `http://127.0.0.1:5187`입니다.

빌드 없이 실행하려면 [Windows Actions](https://github.com/GyutaeLee/wpf-demo/actions/workflows/windows.yml)의 성공한 실행에서 `wpf-demo-windows` 산출물을 받습니다. 같은 실행에서 받은 `api/WpfDemo.Api.exe`와 `client/WpfDemo.exe`를 사용하고 API를 먼저 켭니다. WPF 앱에는 .NET Framework 4.8 이상이 필요하며 API는 Windows x64 자체 포함 배포본입니다.

## 테스트

```sh
dotnet test tests/WpfDemo.Tests/WpfDemo.Tests.csproj -c Release
```

MSTest로 ViewModel 상태와 API 요청·응답을 검사합니다. 현재 변경은 macOS에서 16개 테스트가 통과했으며 Windows CI는 확인 전입니다. [공개 코드의 Windows CI](https://github.com/GyutaeLee/wpf-demo/actions/runs/36147599201)에서는 WPF 빌드와 조회·편집·저장, API 중단 후 재시도를 확인했습니다.

Mac 테스트와 UTM 실행 절차, 아직 확인하지 못한 배율·접근성 항목은 [테스트 방법](docs/testing.md)에 정리했습니다.
