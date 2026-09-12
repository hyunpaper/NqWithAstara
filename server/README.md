# server — .NET 백엔드

Toss Open API 실시간 신호·시뮬레이션 엔진 (.NET 9). 컨벤션 원본은 루트 `README.md`.

## 계층 지도
- `Api/` — HTTP 엔드포인트(검증·응답 매핑만)
- `Application/` — 유스케이스·폴링·조회 서비스 (`Astra.Application.csproj`)
- `Domain/` — 구조 엔진·시뮬레이션·공용 모델 (`Astra.Domain.csproj`)
  - `Structure/`·`Indicators/` v5 구조 엔진, `Confluence/` 다중 분석법 컨플루언스 점수(K2, 관측·표시 전용)
  - `Legacy/` v4 지표·레벨(청산 경로 전용)
- `Infrastructure/` — Toss 게이트웨이(`Toss/`)·영속화·뉴스/관측 스토어
- `Hosting/` — MonitorService·NewsService 등 hosted-service 어댑터
- `Program.cs` DI 구성만, `tests/` — `Astra.Server.Tests.csproj`

## 실행
```bash
dotnet run --project server
```
CWD는 저장소 루트 기준. `ASTRA_CLIENT_ROOT`로 `client/dist` 위치 지정.
설정은 `server/appsettings.json`(gitignored, `appsettings.example.json` 참고).

## 자격증명
`tossapi.txt`(`TOSS_CREDENTIALS_PATH`로 경로 지정) — Toss API 키. 커밋 금지.

## 테스트
```bash
dotnet build server/Astra.Server.csproj -c Release
dotnet test server/tests/Astra.Server.Tests.csproj -v q
```
