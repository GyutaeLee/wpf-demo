# 테스트 방법

macOS에서는 ViewModel과 API 테스트를 실행합니다. WPF 빌드와 화면 실행은 Windows CI에서 확인합니다. UTM 절차는 Mac에서 화면을 직접 확인하기 위한 방법으로 남겼으며 아직 수행하지 않았습니다.

## macOS에서 테스트

[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)를 설치한 뒤 저장소 루트에서 실행합니다.

```sh
dotnet test tests/WpfDemo.Tests/WpfDemo.Tests.csproj -c Release
```

| 대상 | 확인하는 상황 |
| --- | --- |
| ViewModel | 검색·필터·선택, 입력 경계·취소, 로딩과 조회 실패·재시도, 저장 대기·실패·재시도, 저장 후 필터에서 빠진 항목 처리 |
| API | 목록 조회, PUT 후 GET, 잘못된 상태·메모의 400과 기존 값 유지, 메모 200자 경계, 없는 ID의 404 |

ViewModel 테스트는 WPF 앱과 같은 소스를 .NET 10에서 컴파일합니다. API 테스트는 `WebApplicationFactory`의 TestServer를 사용합니다. .NET Framework 4.8과 XAML 바인딩, 실제 TCP 통신은 Windows 실행에서 따로 확인합니다.

### 확인 기록

| 확인 시점·대상 | 결과 |
| --- | --- |
| 2026-09-26, MSTest 전환·추가 시나리오·저장 알림 수정 | macOS 26.6 ARM64 / .NET SDK 10.0.101. ViewModel 9개와 API 7개, 총 16개 통과. |
| 코드 기준 `64e98ed` | [Windows CI 성공](https://github.com/GyutaeLee/wpf-demo/actions/runs/36214253479). MSTest 16개, WPF 빌드, 조회·편집·저장과 API 중단·재시도, 재시도 후 서버 값 확인. |

위 결과는 이번 테스트·클라이언트·API 변경을 확인한 기록입니다. 문서와 캡처 화면은 해당 Windows 실행 결과로 갱신했습니다.

## macOS에서 API 실행

첫 번째 터미널에서 실행합니다.

```sh
dotnet run --project src/WpfDemo.Api/WpfDemo.Api.csproj
```

두 번째 터미널에서 조회 → 수정 → 재조회를 확인합니다.

```sh
curl http://127.0.0.1:5187/api/work-items
curl -X PUT http://127.0.0.1:5187/api/work-items/1001 \
  -H 'Content-Type: application/json' \
  -d '{"status":"진행 중","note":"API 수정 확인"}'
curl http://127.0.0.1:5187/api/work-items
```

API 터미널에서 `Ctrl+C`로 종료합니다. 재시작하면 항목은 초기 상태로 돌아갑니다.

## Windows CI

[워크플로](../.github/workflows/windows.yml)는 테스트 → WPF 빌드 → x64 API 게시 → API·WPF 실행 순서로 구성했습니다. [UI 스크립트](../scripts/windows-ui-smoke.ps1)는 저장·재시도 후 서버 값, 조회 오류·재시도, 일부 Tab 이동·Enter 저장, 작은 창·최대화에서 일부 컨트롤의 창 내부 위치를 검사합니다. 실행 파일, 스크린샷과 로그는 `wpf-demo-windows` 산출물에 담습니다.

현재 워크플로의 테스트·빌드·UI 흐름은 위 실행에서 통과했습니다. 작은 창 검사로 모든 문구의 잘림을 확인할 수 없으며, DPI 기록도 배율 100%·150%에서 직접 사용한 결과는 아닙니다.

## Mac에서 WPF 화면 확인하기

Apple Silicon Mac에서는 UTM으로 Windows 11 ARM 가상 머신을 만들 수 있습니다. [UTM 설치 안내](https://docs.getutm.app/guides/windows/)를 따라 설치합니다.

1. UTM에서 **+ → Virtualize → Windows**를 선택합니다.
2. [Windows 11 ARM64 ISO](https://www.microsoft.com/software-download/windows11arm64)를 사용하고 드라이버·SPICE 도구를 설치합니다. Windows 라이선스도 준비합니다.
3. Windows 설치를 마친 뒤 [Windows Actions](https://github.com/GyutaeLee/wpf-demo/actions/workflows/windows.yml)의 성공한 실행에서 `wpf-demo-windows`를 받아 Windows 안에 압축을 풉니다.
4. 같은 실행에서 받은 `api/WpfDemo.Api.exe`를 먼저 켜고 `client/WpfDemo.exe`를 실행합니다. 실행 파일 확인에 Visual Studio는 필요하지 않습니다.
5. [조회·저장·오류·재시도 흐름](scenarios.md)을 확인하고 아래 수동 확인 결과를 기록합니다.

Windows 11 ARM은 [x86·x64 앱 에뮬레이션](https://learn.microsoft.com/windows/arm/apps-on-arm-x86-emulation)을 지원합니다. Windows 11 22H2 이상에는 [.NET Framework 4.8.1](https://learn.microsoft.com/dotnet/framework/install/on-windows-and-server)이 포함되며 4.8 대상 앱을 실행할 수 있습니다. 현재 x64 API와 WPF의 UTM 동작은 직접 확인 전입니다.

## 수동 확인 목록

| 항목 | 확인할 내용 | 결과 |
| --- | --- | --- |
| UTM | API·WPF 시작, 조회·편집·저장, API 중단·재시도 | 미검증 |
| 1920×1080 / 100% | 최소 창·최대화, 목록·상세의 문구와 버튼 잘림 | 미검증 |
| 1366×768 / 150% | 작업 영역 안에 창이 들어오는지, 문구와 버튼 잘림 | 미검증 |
| 키보드 | Tab·Shift+Tab, Enter 저장·다중 행 메모, Esc 취소 | 일부 CI 경로 확인, 전체 수동 사용은 미검증 |
| Narrator | 입력 레이블·선택·상태 변화 낭독 | 미검증 |
| 고대비 | 텍스트·상태·포커스 가독성 | 미검증 |

직접 확인할 때는 Windows 버전, VM 설정, 해상도·배율, 받은 CI 실행 링크, 재현 순서와 결과를 함께 남깁니다.
