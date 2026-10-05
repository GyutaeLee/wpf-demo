# wpf-demo

장비를 찾아 대여하고 반납 이력을 보는 WPF 앱입니다. 앱은 로컬 API에 연결하며, 화면의 장비와 대여 기록은 모두 가상 데이터입니다.

![장비 대여와 이력 화면](docs/screenshots/lending.png)

![장비 대여·반납 시연](docs/videos/basic.gif)

[대여·반납 영상](docs/videos/basic.mp4) · [응답 유실 후 재시도 영상](docs/videos/lost-response-retry.mp4)

## 기능

- 장비 검색, 상태 필터, 대여·반납, 장비별 이력 조회
- 다른 사용자가 먼저 대여한 경우 충돌을 알리고 최신 정보를 다시 조회
- 저장 요청의 응답이 끊기면 같은 요청으로 재시도하고, 앱을 다시 열어도 보관한 요청 복구
- 장비 10,000개와 이력 20,000건을 페이지 단위로 조회

## Windows에서 실행

[Windows x64 실행 파일 다운로드](https://github.com/GyutaeLee/wpf-demo/releases/latest/download/wpf-demo-windows-demo.zip) 후 압축을 풉니다. PowerShell 창 하나에서 API를 실행하고, 새 창에서 WPF 앱을 실행합니다.

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

Windows x64에서 MSTest 53개와 여섯 가지 UI 시나리오를 확인했습니다. [Windows 실행 결과](https://github.com/GyutaeLee/wpf-demo/actions/runs/37254391635)와 [설계 시나리오](docs/scenarios.md)를 참고하세요.
