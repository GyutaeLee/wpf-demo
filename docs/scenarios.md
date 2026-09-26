# 화면 상태와 테스트

업무 목록을 편집할 때 생길 만한 상황을 가정해 구현하고 테스트했습니다. 운영 장애나 실제 사용자 사례를 뜻하지 않으며, 데이터는 모두 가상입니다.

## 구조

[MainWindow.xaml](../src/WpfDemo.Client/MainWindow.xaml)은 ViewModel의 속성과 Command에 바인딩합니다. [WorklistViewModel](../src/Shared/WorklistViewModel.cs)은 `INotifyPropertyChanged`로 상태 변화를 알리고 `ICommand`로 조회·저장·취소를 연결합니다.

ViewModel은 [IWorkItemApi](../src/Shared/IWorkItemApi.cs)를 호출합니다. 앱에서는 [HttpWorkItemApi](../src/WpfDemo.Client/HttpWorkItemApi.cs)가 HTTP 요청을 보내고, ViewModel 테스트에서는 가짜 API로 응답 시점과 실패를 정합니다. API의 요청 검증은 별도의 통합 테스트에서 확인합니다.

## 저장 응답이 늦게 도착할 때

응답을 기다리는 동안 사용자가 다른 항목을 선택하거나 저장을 다시 누를 수 있습니다. 한 상세 화면에서 한 건씩 처리하도록 선택·검색·필터·새로고침·추가 저장·취소를 막았습니다. 편집 입력은 XAML의 `CanEdit` 바인딩으로 비활성화합니다. ViewModel의 편집 속성 setter가 직접 변경을 거부하는 구조는 아닙니다.

`TaskCompletionSource`로 응답 시점을 정한 [ViewModel 테스트](../tests/WpfDemo.Tests/WorklistViewModelTests.cs)에서 선택과 초안 유지, 추가 조회 차단, 수정 요청이 한 번만 호출되는지 확인했습니다. 응답이 도착하면 다시 선택·편집할 수 있습니다. 여러 항목의 동시 편집 대신 현재 입력을 보호하는 쪽을 선택했습니다.

이 검사는 한 ViewModel 안의 동작을 확인합니다. 두 앱의 동시 수정이나 저장 응답만 유실되는 상황은 다루지 않습니다. 응답 대기 중 입력 컨트롤의 실제 비활성화는 별도 화면 확인이 필요합니다.

## 저장 실패 후 다시 시도할 때

API에 연결하지 못해도 상태와 메모 초안은 남겨 둡니다. 사용자는 API를 다시 실행한 뒤 같은 화면에서 저장할 수 있습니다. 조회 실패는 빈 검색 결과와 구분해 표시합니다.

[ViewModel 테스트](../tests/WpfDemo.Tests/WorklistViewModelTests.cs)는 저장 실패 후 입력 유지·재시도와 조회 실패·재시도를 확인합니다. [Windows 스크립트](../scripts/windows-ui-smoke.ps1)는 API 프로세스를 직접 중지·재시작해 화면 오류와 재시도를 확인합니다. 재시도 후 GET으로 서버 메모까지 확인하는 검사도 [Windows CI](https://github.com/GyutaeLee/wpf-demo/actions/runs/36214253479)에서 통과했습니다.

![API 중단 후 저장 실패 화면](screenshots/save-error.png)

화면은 같은 [Windows CI](https://github.com/GyutaeLee/wpf-demo/actions/runs/36214253479)에서 캡처했습니다. 초안은 앱 종료 후에는 남지 않으며, API도 재시작하면 초기 데이터로 돌아갑니다.

## 저장한 항목이 필터에서 빠질 때

‘대기’ 필터에서 항목을 ‘진행 중’으로 저장하면 해당 항목을 목록에서 빼고 선택을 해제합니다. 저장 결과는 계속 표시합니다.

추가한 테스트에서 선택 해제와 함께 저장 알림도 지워지는 동작을 확인했습니다. [SaveAsync](../src/Shared/WorklistViewModel.cs)에서 필터 갱신 뒤 알림을 설정하도록 순서를 바꿨습니다. 수정 후 빈 목록·선택 해제·편집 완료 상태·알림 유지 검사가 통과했습니다. 필터에서 빠진 항목의 실제 화면 표시는 별도 확인 전입니다.

## API에 잘못된 값을 보낼 때

화면 검증을 거치지 않은 요청도 받으므로 API에서 상태와 메모를 검사합니다. 잘못된 상태, 공백만 있는 메모, 201자 메모는 400으로 거부합니다. 빈 메모와 200자 메모는 허용하며 없는 ID에는 404를 반환합니다.

[API 통합 테스트](../tests/WpfDemo.Tests/ApiIntegrationTests.cs)는 각 호스트를 따로 만들어 수정 응답 뒤 GET으로 값을 확인합니다. 거부한 요청도 다시 조회해 기존 상태·메모가 유지되는지 검사합니다. 같은 호스트의 메모리 상태를 확인하는 테스트이며 서버 재시작 후 영속성 검사는 아닙니다.

## 직접 확인하기

같은 CI 실행에서 받은 API와 WPF 앱을 사용합니다.

1. API와 WPF를 켜고 항목 1001의 메모를 수정해 저장합니다.
2. API를 종료한 뒤 메모를 다시 수정하고 저장합니다. 저장 오류와 입력 유지를 확인합니다.
3. API를 다시 켜고 저장을 재시도합니다. 조회 API의 항목 1001 메모가 화면과 같은지 확인합니다.
4. 상태 필터를 ‘대기’로 바꾸고 대기 항목을 ‘진행 중’으로 저장합니다. 목록에서 빠지고 선택이 해제돼도 저장 알림이 남는지 확인합니다. 마지막 단계는 이번 수정분을 포함한 실행 파일이 필요합니다.

Mac에서 Windows를 실행하는 방법과 테스트 결과는 [테스트 방법](testing.md)에 있습니다.
