# 실행과 확인

## Windows에서 실행

1. [확인한 Windows Actions 실행](https://github.com/GyutaeLee/wpf-demo/actions/runs/36323609412)을 엽니다.
2. `wpf-demo-windows` 산출물을 내려받아 압축을 풉니다.
3. 안에 있는 `wpf-demo-windows-demo.zip`도 풉니다.
4. 첫 PowerShell 창에서 API를 실행합니다.
5. 두 번째 PowerShell 창에서 WPF 앱을 실행합니다.

```powershell
.\api\WpfDemo.Api.exe
```

```powershell
.\client\WpfDemo.exe
```

API는 `http://127.0.0.1:5187`에서 요청을 받습니다. API는 Windows x64 자체 포함 배포본입니다. WPF 앱을 실행하려면 .NET Framework 4.8 이상이 필요합니다. 실행 파일 사용에는 Visual Studio가 필요하지 않습니다.

## 자동 시나리오

자동 시연을 실행하려면 PowerShell 7과 WinAppCLI 0.7.0을 설치합니다. 압축을 푼 패키지의 최상위 폴더에서 다음 명령을 실행합니다.

```powershell
pwsh -ExecutionPolicy Bypass -File .\scripts\Start-Demo.ps1 -Scenario Basic
pwsh -ExecutionPolicy Bypass -File .\scripts\Start-Demo.ps1 -Scenario All
```

`Basic`은 대여와 반납을 실행합니다. `All`은 정상 처리, 두 클라이언트의 대여 충돌, 저장 응답 유실, API 연결 실패, 앱 재시작 후 요청 복구를 차례로 시연합니다. 각 실행은 별도 서버·클라이언트 데이터 폴더와 결과 파일, 화면 캡처, 녹화 영상을 만듭니다. 기존 기본 데이터 폴더는 초기화하지 않습니다.

## 테스트와 확인 범위

저장소 루트에서 자동 테스트를 실행합니다.

```sh
dotnet test tests/WpfDemo.Tests/WpfDemo.Tests.csproj -c Release
```

ViewModel 테스트는 검색·필터, 입력 검증, 처리 중 잠금, 오류와 재시도, 재시작 복구를 확인합니다. API 통합 테스트는 HTTP 요청을 보내 동시 대여, 중복 요청, 저장 롤백, 데이터 유지와 잘못된 입력을 검사합니다.

macOS에서 실행한 테스트는 .NET 10 테스트 프로젝트를 대상으로 합니다. 이 결과만으로 .NET Framework 4.8 WPF 빌드나 실제 Windows 화면까지 확인한 것은 아닙니다.

| 확인 항목 | 상태 |
| --- | --- |
| macOS .NET 테스트 | 2026-09-27, MSTest 32개 통과 |
| Windows x64 빌드·테스트·시연 | [Actions 실행](https://github.com/GyutaeLee/wpf-demo/actions/runs/36323609412), MSTest 32개와 다섯 시나리오 통과 |
| 키보드와 창 크기 | Tab·Enter·Esc, 작은 창과 최대화의 UI 요소 좌표를 자동 검사 |
| 100%·150% 배율, Narrator, 고대비 | Windows에서 직접 확인 전 |

Windows workflow에는 WPF 빌드와 다섯 시나리오 실행이 들어 있습니다. 통과한 실행의 산출물에는 그 실행에서 만든 캡처·영상과 실행 파일이 함께 올라갑니다. 작은 창 검사는 모든 문구와 실제 디스플레이 배율에서의 가독성까지 확인하지 않습니다.

## 화면과 영상

같은 Actions 산출물의 `runs` 폴더에는 시나리오별 `result.json`, `screenshots`, `videos`가 있습니다. `result.json`에 장비 상태·대여 이력 건수·작업 키를 기록했습니다. 영상은 H.264 MP4이며 30~60초 길이입니다. 길이와 시작·중간·끝 프레임을 확인했습니다.

`Basic`은 대여·반납, `ConcurrentLoan`은 오래된 조회 결과의 충돌, `LostResponse`와 `ApiDown`은 같은 요청으로 재시도하는 흐름입니다. `RestartRecovery` 영상 두 개는 종료 전과 복구 후 화면이며, 같은 작업 키와 이력 한 건으로 연결됩니다.
