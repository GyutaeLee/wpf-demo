# wpf-demo

장비를 찾아 대여하고 반납 이력을 보는 WPF 앱입니다. 앱은 로컬 API에 연결하며, 화면의 장비와 대여 기록은 모두 가상 데이터입니다.

![장비 대여와 이력 화면](docs/screenshots/lending.png)

![장비 대여·반납 시연](docs/videos/basic.gif)

[대여·반납 영상](docs/videos/basic.mp4) · [응답 유실 후 재시도 영상](docs/videos/lost-response-retry.mp4)

## 기능

- 장비 검색, 상태 필터, 대여·반납, 장비별 이력 조회
- 다른 사용자가 먼저 대여한 경우 충돌을 알리고 최신 정보를 다시 조회
- 저장 요청의 응답이 끊기면 같은 요청으로 재시도하고, 앱을 다시 열어도 보관한 요청 복구
- API 연결이 끊겼을 때 이전 조회 결과 표시, 진단 ZIP에서 요청 본문·메모·검색어 제외
- 장비 10,000개와 이력 20,000건을 페이지 단위로 조회

## Windows에서 실행

[최신 Windows x64 Release](https://github.com/GyutaeLee/wpf-demo/releases/latest)를 열어 설명을 확인한 뒤 `wpf-demo-windows-demo.zip`을 내려받아 압축을 풉니다. PowerShell 창 하나에서 API를 실행하고, 새 창에서 WPF 앱을 실행합니다.

API:

```powershell
.\api\WpfDemo.Api.exe
```

WPF 앱:

```powershell
.\client\WpfDemo.exe
```

WPF 앱을 실행하려면 .NET Framework 4.8 이상이 필요합니다. 자세한 절차는 [테스트 안내](docs/testing.md)에 있습니다.

## 기술 구성

WPF 앱은 .NET Framework 4.8과 XAML, 직접 작성한 MVVM 구조로 만들었습니다. `StatusBadge`는 목록과 상세에서 쓰는 Custom Control입니다. 서버는 ASP.NET Core .NET 10 Minimal API이며 SQLite에 장비와 대여 이력을 저장합니다.

확인한 Windows 실행에서는 .NET Framework 4.8 빌드, MSTest 56개와 여섯 UI 시나리오가 통과했습니다. 현재 워크플로는 Actions에서 직접 실행해야 합니다. [Windows 실행 기록](https://github.com/GyutaeLee/wpf-demo/actions/runs/37271558473)과 [Release](https://github.com/GyutaeLee/wpf-demo/releases/latest)를 볼 수 있습니다.

설계와 범위는 [시나리오 문서](docs/scenarios.md), 실행 절차와 검증 범위는 [테스트 안내](docs/testing.md)에 정리했습니다. 실제 Windows에서 확인할 항목은 [수동 확인 목록](docs/manual-windows-check.md)을 참고하세요.
