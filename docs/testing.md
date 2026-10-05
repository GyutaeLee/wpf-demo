# 실행과 확인

## Windows에서 앱 실행

1. [최신 Windows x64 Release](https://github.com/GyutaeLee/wpf-demo/releases/latest)에서 `wpf-demo-windows-demo.zip`을 내려받아 압축을 풉니다.
2. 압축을 푼 폴더에서 PowerShell 창 두 개를 엽니다.
3. 첫 창에서 API를 실행하고, 두 번째 창에서 WPF 앱을 실행합니다.

```powershell
.\api\WpfDemo.Api.exe
```

```powershell
.\client\WpfDemo.exe
```

API는 `http://127.0.0.1:5187`에서 실행됩니다. API에는 런타임이 포함되어 있고, WPF 앱에는 .NET Framework 4.8 이상이 필요합니다. Visual Studio는 필요하지 않습니다. 기본 데이터는 `%LOCALAPPDATA%\wpf-demo\server`와 `%LOCALAPPDATA%\wpf-demo\client`에 각각 저장됩니다. 서버 DB를 유지하면 대여·반납 결과도 다음 실행에 남습니다.

## GitHub Actions에서 Windows 화면 보기

현재 Windows 워크플로는 자동으로 push나 pull request마다 실행되지 않습니다. [Windows workflow](https://github.com/GyutaeLee/wpf-demo/actions/workflows/windows.yml)에서 `Run workflow`를 눌러 `main`을 선택하면 Windows 러너가 빌드·테스트와 여섯 UI 시나리오를 실행합니다.

실행 결과 페이지에서 `wpf-demo-windows` 아티팩트를 내려받습니다. 실행 ZIP, 테스트 결과와 시나리오별 `runs` 폴더가 들어 있습니다. 성공한 시나리오에는 PNG·MP4와 `result.json`이 포함됩니다. 아티팩트는 해당 Actions 실행에 연결된 파일이며 저장소나 Release에 자동으로 추가되지 않습니다. 저장소의 `docs/videos`에 있는 짧은 예시 영상은 별도로 저장한 파일입니다. 아티팩트는 기본 90일 보관되며 저장소 설정에 따라 기간이 달라질 수 있습니다. [GitHub 보관 안내](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/remove-workflow-artifacts)

Mac에서도 캡처와 영상을 내려받아 볼 수 있지만, 러너 화면을 실시간으로 조작할 수는 없습니다. 쓰기 권한이 있으면 위 페이지에서 새 실행을 시작할 수 있습니다. [마지막으로 문서에 확인한 성공 실행](https://github.com/GyutaeLee/wpf-demo/actions/runs/37271558473)은 MSTest 56개와 여섯 UI 시나리오를 통과했습니다.

## Windows에서 자동 시나리오 실행

자동 시연에는 PowerShell 7과 WinAppCLI 0.7.0이 필요합니다. WinAppCLI는 `winget install --id Microsoft.WinAppCLI --version 0.7.0`으로 설치할 수 있습니다. Release ZIP의 최상위 폴더에서 실행합니다.

```powershell
pwsh -ExecutionPolicy Bypass -File .\scripts\Start-Demo.ps1 -Scenario Basic
pwsh -ExecutionPolicy Bypass -File .\scripts\Start-Demo.ps1 -Scenario All
```

`All`은 `Basic`, `ConcurrentLoan`, `LostResponse`, `ApiDown`, `RestartRecovery`, `LargeList`를 차례로 실행합니다. 각 실행은 `runs` 아래 새 폴더에 결과와 캡처를 남기며, 대량 목록 시나리오는 별도 데이터베이스에 장비 10,000개와 이력 20,000건을 준비합니다. 고정 규칙으로 생성하므로 데이터는 무작위로 바뀌지 않습니다. 스크립트는 포트 `5187`을 이미 사용 중이면 기존 프로세스를 건드리지 않고 중단합니다.

## 테스트와 검증 범위

저장소 루트에서 ViewModel·API 테스트를 실행합니다.

```sh
dotnet test tests/WpfDemo.Tests/WpfDemo.Tests.csproj -c Release
```

이 테스트 프로젝트는 .NET 10을 대상으로 합니다. macOS에서 통과해도 .NET Framework 4.8 WPF 빌드나 Windows 화면을 확인한 것은 아닙니다. Windows Actions는 별도로 WPF 앱을 빌드하고 UI 시나리오를 실행합니다.

| 확인 항목 | 자동 확인 | 남은 확인 |
| --- | --- | --- |
| ViewModel·API | 검색·필터, 입력 검증, 중복·동시 요청, 저장·복구 흐름 | — |
| WPF 화면 | .NET Framework 4.8 빌드, 키보드 흐름, 작은 창과 최대화 상태의 주요 컨트롤 위치 | 실제 화면에서 글자가 잘 읽히는지 |
| Windows 접근성 | 자동 UI 조작과 요소 이름 일부 | 100%·150% 배율, Narrator, 고대비는 [수동 확인 목록](manual-windows-check.md) 참고 |

작은 창 검사는 주요 컨트롤이 창 안에 있는지 검사합니다. 실제 디스플레이 배율별 가독성을 보장하지는 않습니다.

대량 목록 API는 Windows x64 러너에서 준비 요청 2회 뒤 7회 측정합니다. 2026-10-05 실행의 목록·이력 조회 중앙값은 각각 2.27ms와 18.47ms였습니다. 한 러너의 로컬 HTTP 측정이며 UI 응답 시간이나 이전 구현 대비 개선을 뜻하지 않습니다. 측정값과 환경은 Actions 아티팩트의 `performance/large-profile.json`에서 확인할 수 있습니다.
