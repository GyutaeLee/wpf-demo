# wpf-demo

WPF 데스크톱 업무 목록과 로컬 HTTP API를 연결한 예제입니다. 목록을 검색하고 상태로 거른 뒤, 항목의 상태와 메모를 수정해 저장하거나 취소할 수 있습니다.

## Windows 화면

![메모를 수정하고 저장한 화면](docs/screenshots/saved.png)

API 연결이 끊기면 입력 내용을 유지한 채 저장 실패를 표시합니다.

![API 중단 후 저장 실패 메시지를 표시한 화면](docs/screenshots/save-error.png)

## 구성

- `src/WpfDemo.Client`: .NET Framework 4.8 WPF 앱. XAML 바인딩과 ViewModel이 검색, 선택, 입력 검증, 저장 상태를 처리합니다.
- `src/Shared`: 클라이언트와 테스트가 함께 사용하는 모델과 ViewModel.
- `src/WpfDemo.Api`: .NET 10 Minimal API. 메모리에 업무 항목 5개를 두고 `GET /api/work-items`, `PUT /api/work-items/{id}`를 제공합니다. 서버를 다시 시작하면 초기 데이터로 돌아갑니다.
- `StatusBadge`: dependency property와 `ControlTemplate`을 사용하는 WPF Custom Control. 목록과 상세 화면에서 같은 컨트롤을 씁니다.
- `tests/WpfDemo.Tests`: 검색, 선택, 입력 검증, 저장, 오류와 재시도를 검사합니다.

## Windows에서 실행

빌드하려면 Visual Studio의 **.NET 데스크톱 개발** 워크로드와 .NET Framework 4.8 타기팅 팩, .NET 10 SDK가 필요합니다.

1. `wpf-demo.sln`을 Visual Studio에서 엽니다.
2. 다음 명령으로 API를 실행합니다. API는 `http://127.0.0.1:5187`에서 요청을 받습니다.

   ```powershell
   dotnet run --project src/WpfDemo.Api/WpfDemo.Api.csproj
   ```

3. Visual Studio에서 `WpfDemo.Client`를 시작 프로젝트로 실행합니다.

Visual Studio 없이 실행하려면 GitHub Actions의 `wpf-demo-windows` 산출물을 내려받습니다. `api/WpfDemo.Api.exe`를 먼저 실행하고 `client/WpfDemo.exe`를 실행합니다. API는 Windows x64 자체 포함 배포본이며, WPF 앱에는 .NET Framework 4.8 이상이 필요합니다.

## macOS에서 테스트

macOS에서는 ViewModel 테스트와 API를 실행할 수 있습니다. WPF 화면은 Windows 전용이라 이 단계에서는 직접 열리지 않습니다.

### ViewModel과 API 확인

먼저 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)를 설치한 뒤 저장소 루트에서 테스트를 실행합니다.

```sh
dotnet run --project tests/WpfDemo.Tests/WpfDemo.Tests.csproj
```

테스트는 검색·상태 필터·선택·입력 검증·취소·저장 성공·실패 후 재시도를 확인합니다. 여기서 컴파일하는 것은 공유 ViewModel 코드이며 WPF 화면이나 .NET Framework 앱 자체를 macOS에서 실행하는 테스트는 아닙니다.

API는 별도 터미널에서 실행합니다.

```sh
dotnet run --project src/WpfDemo.Api/WpfDemo.Api.csproj
```

다른 터미널에서 목록 조회와 수정을 확인할 수 있습니다.

```sh
curl http://127.0.0.1:5187/api/work-items
curl -X PUT http://127.0.0.1:5187/api/work-items/1001 \
  -H 'Content-Type: application/json' \
  -d '{"status":"진행 중","note":"API 수정 확인"}'
```

수정 내용은 메모리에만 남으므로 서버를 종료하면 처음 데이터로 돌아갑니다. 종료는 API 터미널에서 `Ctrl+C`를 누릅니다.

### Mac에서 WPF 화면 확인하기 (UTM)

Apple Silicon Mac에서는 UTM으로 Windows 11 ARM 가상 머신을 만들 수 있습니다. [UTM의 Windows 설치 안내](https://docs.getutm.app/guides/windows/)에 따라 UTM에서 **+ → Virtualize → Windows**를 선택하고, [Microsoft Windows 11 ARM64 ISO](https://www.microsoft.com/software-download/windows11arm64)로 설치합니다. Windows를 실행하려면 유효한 Windows 라이선스가 필요합니다.

1. GitHub의 [Windows Actions 실행 목록](https://github.com/GyutaeLee/wpf-demo/actions/workflows/windows.yml)에서 가장 최근 성공한 실행을 엽니다.
2. `wpf-demo-windows` 산출물을 내려받아 Windows 가상 머신 안에서 압축을 풉니다.
3. 압축을 푼 폴더에서 `api/WpfDemo.Api.exe`를 실행한 다음 `client/WpfDemo.exe`를 실행합니다.
4. 목록 조회, 검색·상태 필터, 항목 편집·저장을 확인합니다. API 프로세스를 종료해 오류 표시를 확인한 뒤 API를 다시 켜고 재시도합니다.
5. 창 크기를 바꾸고 Tab·Enter 키를 사용해 봅니다. Windows 디스플레이 배율 100%와 150%에서 화면이 잘리지 않는지도 확인합니다.

Windows 11 ARM은 x64 프로그램을 에뮬레이션할 수 있으므로 x64로 게시한 API도 실행 대상입니다. 이 가상 머신 검증은 CI의 Windows x64 실행과 별개입니다. 직접 확인하기 전에는 UTM에서의 동작을 검증 완료로 간주하지 않습니다. [.NET Framework 4.8.1은 .NET Framework 4 앱과 호환](https://learn.microsoft.com/dotnet/framework/install/on-server-2019)되고 Windows 11에 포함됩니다.

## Windows CI 확인

GitHub Actions는 Windows에서 .NET Framework 4.8 WPF 클라이언트를 빌드하고, API와 앱을 함께 실행해 주요 화면 흐름을 확인합니다. 캡처 화면과 실행 파일은 `wpf-demo-windows` 산출물로 저장됩니다.
