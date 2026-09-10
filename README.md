# NqWithAstara — 개발 가이드

미국 정규장 관심종목의 실시간 신호·시뮬레이션 로컬 앱 (.NET 9 + React/Vite + Toss Open API).
이 문서는 **개발자·에이전트(Claude/Codex)가 공유하는 단일 컨벤션 문서**다. 여기 없는 규칙은 규칙이 아니다.

---

## 1. 역할 체계

```mermaid
flowchart LR
    H[사람<br>방향 결정·최종 책임] --> S[감독자 에이전트<br>Claude/Codex 메인 세션]
    S -->|작업 지시| W1[하위 에이전트 A]
    S -->|작업 지시| W2[하위 에이전트 B]
    W1 & W2 -->|브랜치 push + PR| R{감독자<br>코드 리뷰}
    R -->|승인| M[rebase merge]
    R -->|반려| W1
```

- 하위 에이전트가 개발하고, **감독자(메인 에이전트)는 코드를 직접 읽고 리뷰한다.**
- CI 통과는 머지의 **필요조건이지 충분조건이 아니다.** 리뷰 없이 머지 금지.

## 2. 브랜치 전략

```mermaid
gitGraph
    commit id: "init"
    branch develop
    checkout develop
    commit id: "integration"
    branch feature/BE/zone-engine
    commit id: "work"
    commit id: "work2"
    checkout develop
    merge feature/BE/zone-engine id: "rebase-merge"
    branch release/COMMON/v1-0-0
    commit id: "release-prep"
    checkout main
    merge release/COMMON/v1-0-0 id: "release"
```

| 브랜치 | 역할 | 규칙 |
|---|---|---|
| `master` | 릴리즈 | `release/*` PR만 받는다. develop을 직접 머지하지 않는다 |
| `develop` | 통합 (기본 브랜치) | 모든 작업 PR의 대상. **직접 push 금지 — 무조건 브랜치를 딴다** |
| 작업 브랜치 | 단위 작업 | 항상 최신 `develop`에서 분기 |

### 브랜치 이름 — CI가 정규식으로 강제

```text
<type>/<area>/<slug>
```

| 요소 | 허용 값 |
|---|---|
| `type` | `feature` `fix` `refactor` `release` |
| `area` | `BE` `FE` `COMMON` |
| `slug` | 소문자 kebab-case (`[a-z0-9]+(-[a-z0-9]+)*`) |

예: `feature/FE/structure-chart` · `fix/BE/polling-timeout` · `release/COMMON/v1-2-0`

## 3. 커밋 컨벤션 + 에이전트 구분자

### 커밋 제목

```text
<type>(<AREA>): <한 줄 요약> [<agent>]
```

- `type`/`AREA`는 브랜치와 동일한 어휘.
- **`[<agent>]` 태그는 자동화 에이전트가 만든 커밋에 필수**: `[claude]` 또는 `[codex]`. 사람이 직접 만든 커밋은 태그를 생략한다.

### 커밋 트레일러 (본문 마지막)

```text
Agent: claude            # 또는 codex — 에이전트 커밋 필수 (기계 판독용)
Co-Authored-By: ...      # 각 도구의 기본 서명은 그대로 유지
```

예시:

```text
fix(BE): CI 아티팩트 실행에서 v5 소스 스캔 경로 해석 실패 수정 [claude]

ServerRoot()가 BaseDirectory 상위만 탐색해 artifacts 실행 시 서버 루트를 찾지 못했다.
각 상위 디렉터리의 server/ 하위도 함께 확인한다.

Agent: claude
Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
```

### PR·리뷰·머지의 주체 표기

| 시점 | 표기 (PR 코멘트/본문) |
|---|---|
| PR 생성 | 본문 마지막 줄 `Agent: claude` 또는 `Agent: codex` (사람이 열면 생략) |
| 리뷰 | `review by <agent>: approve` 또는 `review by <agent>: request-changes — <사유>` |
| 머지 | `merged by <agent> — CI green + review approved` |

단일 GitHub 계정 환경이라 공식 approve 버튼 대신 **위 코멘트가 리뷰 증적**이다.

## 3.5 이슈 워크플로우 (작업 히스토리의 시작점)

**모든 작업은 이슈에서 시작한다.** 이슈 없이 브랜치 만들지 않는다.

```mermaid
flowchart LR
    I[이슈 생성<br>배경·범위·완료조건] --> BR["브랜치 분기<br>&lt;type&gt;/&lt;area&gt;/&lt;이슈#&gt;-&lt;slug&gt;"]
    BR --> PR["PR 본문에 Closes #N"]
    PR --> M[머지] --> C[이슈 자동 close<br>타임라인에 전체 이력]
```

| 규칙 | 내용 |
|---|---|
| 이슈 제목 | `[영역] 요약` — 본문에 배경 / 작업 범위 / 완료 조건 |
| 라벨 | 아래 라벨 체계에서 성격·영역·도메인 선택. 에이전트 작업이면 `agent:claude`/`agent:codex` |
| 마일스톤 | 해당 주차 마일스톤 배정 — **주간, 월요일 시작**, 제목 `2026-Wnn (MM/DD ~ MM/DD)`. 없으면 생성 |
| **어싸인** | **항상 `hyunpaper`** (에이전트는 계정을 공유하므로 주체 구분은 라벨·`Agent:` 표기로) |
| 브랜치 | slug 앞에 이슈 번호: `refactor/COMMON/8-issue-workflow-docs` |
| PR | 본문에 `Closes #N` — develop 머지 시 이슈 자동 close |

### 라벨 체계

| 분류 | 라벨 |
|---|---|
| 성격 | `bug` `feature` `refactor` `test` `ci` `docs` `chore` `hotfix` |
| 영역 | `back-end` `front-end` `common` |
| 도메인 | `structure-engine` `simulation` |
| 메타 | `priority-high` `blocked` `agent:claude` `agent:codex` |

성격 라벨과 브랜치 `type`은 별개 축이다 (예: `bug` 이슈 → `fix/...` 브랜치). 라벨 중복 금지 — 같은 의미의 라벨을 새로 만들지 않는다.

## 4. PR → CI → 리뷰 → 머지 파이프라인

```mermaid
flowchart LR
    A[develop에서<br>브랜치 분기] --> B[작업 + 커밋]
    B --> C[최신 develop 위로<br>rebase 후 push]
    C --> D[PR 생성 → develop]
    D --> E{CI: init / build / test}
    E -->|실패| B
    E -->|전부 green| R{감독자 코드 리뷰}
    R -->|request-changes| B
    R -->|approve 코멘트| F[Rebase and merge]
    F --> G[브랜치 자동 삭제]
```

### 머지 규칙

- **Rebase and merge만 사용한다.** merge commit·squash 금지 (저장소 설정으로 비활성화됨).
- CI 3단계 전부 통과 + **감독자 리뷰 approve 코멘트** 없이는 머지하지 않는다.
- 릴리즈: `develop` → `release/<area>/<slug>` 브랜치 → `master` PR. rebase로 master 해시가 바뀌므로 **master를 develop으로 역머지하지 않는다** — develop이 유일한 통합 히스토리다.
- 머지 후 작업 브랜치는 삭제된다 (자동).

## 5. 아키텍처

```mermaid
flowchart TB
    subgraph EXT[외부]
        TOSS[Toss REST / WebSocket]
        FS[(로컬 저장 App_Data)]
    end
    subgraph HOST[Host — server/]
        PROG[Program.cs — DI 구성만]
        API[Api/ApiEndpoints.cs — 요청 검증·응답 매핑만]
    end
    subgraph APP[Application — server/Application]
        POLL[MonitorPollingService<br>15초 폴링 · gate · generation]
        QRY[조회 서비스<br>State / Metrics / Liquidity / SimReport]
        SAS[StructureAnalysisService<br>v5 snapshot · 모드 게이트]
    end
    subgraph DOM[Domain — server/Domain]
        V5[Structure 엔진<br>집계→피벗→Zone→추세→후보→계획]
        SIM[SimulationEngine · SignalLifecycle<br>시뮬 체결·수명 관리]
    end
    subgraph INFRA[Infrastructure]
        GW[Toss gateway 어댑터]
        OBS[StructureObservationStore]
    end
    FE[client/ React] --> API
    API --> APP
    POLL --> SIM
    SAS --> V5
    APP --> INFRA
    GW --> TOSS
    INFRA --> FS
```

**의존 방향 (테스트로 강제 — ArchitectureBoundaryTests)**: `Host → Application → Domain`. Domain은 HTTP·파일·DI·시계 조회에 의존하지 않는다 (asOf/policy 명시 전달).

### 구조 엔진 롤아웃 (`StructureEngineMode`)

| 모드 | 동작 |
|---|---|
| `off` (기본) | 구조 엔진 비활성 |
| `shadow` | 관측 전용 — simtrades/positions/알림에 **무쓰기** (바이트 동일성 테스트로 증명) |
| `active` | 구조 엔진이 신규 진입 소유. 이미 열린 거래는 체결 시점의 FrozenPlan대로 청산 |

- 구조 근거가 없으면 **진입 보류가 정답** — ATR 배수/1.5R 폴백으로 목표·손절을 만들지 않는다.
- 구조 엔진 소스는 레거시 진입점(`MarketRules.Enter`/`PriceLevels.*` 등)을 참조하지 않는다 (정적 테스트로 강제).

## 6. 로컬 검증 (PR 전 필수)

CI와 동일 스택: Node.js 22, .NET 9.

```sh
node scripts/repository-policy.mjs                     # 비밀/금지 경로 스캔
node --test scripts/repository-policy.test.mjs
dotnet test server/tests/Astra.Server.Tests.csproj --configuration Release
cd client && npm ci && npm run build
```

앱 실행/종료: `scripts/Start-Astra.ps1` / `scripts/Stop-Astra.ps1` (로컬 전용).

## 7. 커밋 금지 대상

`.gitignore` + `scripts/repository-policy.mjs`(CI에서 실행)가 이중으로 막지만, 규칙으로도 명시한다:

| 분류 | 대상 |
|---|---|
| 자격증명 | `tossapi.txt` `tossss.txt` `.env*` `*.pem` `*.key`, 모든 토큰 문자열 |
| 런타임 데이터 | `App_Data/` `.runtime/` 로그 |
| 로컬 설정 | `appsettings*.json` (**`appsettings.example.json`만 커밋**) |
| 산출물 | `bin/` `obj/` `dist/` `node_modules/` `TestResults/` |
| 작업 문서 | `WORK-LOG*.md` `ITERATIONS.md` `STRUCTURE-ENGINE-*.md` `REVIEW-*.md` `research/` 등 로컬 개발 로그 |
| 로컬 런처 | `*.cmd` |
| 에이전트 상태 | `.claude/` `.codex/` 등 |

테스트는 라이브 외부 API에 의존하면 안 된다 (fake gateway/TimeProvider 사용).
