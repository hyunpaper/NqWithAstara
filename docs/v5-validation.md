# v5 유효성 검증 — 데이터 계약 · 한계 · 평가 절차

이슈 #28. 이 문서는 v5 구조 엔진의 **수익성·점수 유효성을 어떻게 검증하는지**, 그리고 **현재 데이터로 무엇을
검증할 수 없는지**를 함께 고정한다. 테스트 통과나 shadow 기록 정합성은 유효성의 근거가 아니다.

> 이 경로는 **읽기 전용**이다. 운영 진입/청산·점수 공식·호가 결측 정책을 바꾸지 않고 어떤 파일에도 쓰지 않는다.
> 정책 변경 제안은 이 문서의 근거를 인용해 **별도 이슈·별도 승인**으로 진행한다.

---

## 1. 소유 파일

| 계층 | 파일 | 역할 |
|---|---|---|
| Domain | `server/Domain/Validation/ValidationContracts.cs` | 평탄한 입력 행·연결 결과·한계 코드 |
| Domain | `server/Domain/Validation/CandidateTradeLinker.cs` | 후보→결정→거래→결과 연결, 중복 제거, 시간 절단 |
| Domain | `server/Domain/Validation/ValidationEvaluator.cs` | 사전 정의 코호트 평가, 표본 충분성, 불확실성 |
| Domain | `server/Domain/Validation/WalkForwardEvaluator.cs` | 시간 순서 학습/검증 분리 |
| Domain | `server/Domain/Validation/CostSensitivity.cs` | 보수적 비용 시나리오(별도 필드) |
| Application | `server/Application/ValidationQueryService.cs` | 관측 파일·거래 저장소 읽기, 데이터 감사, 보고서 조립 |
| Api | `GET /api/validation?days=N` | additive 조회 엔드포인트 (기본 30일, 최대 180일) |

Domain은 시계·저장소·HTTP에 의존하지 않는다. 평가 기준 시각(`AsOf`)은 항상 Application이 명시적으로 넘긴다.

---

## 2. 데이터 계약

### 2.1 원천

| 원천 | 위치 | 성격 |
|---|---|---|
| 관측 | `App_Data/structure/structure-observations-YYYY-MM-DD.jsonl` | 한 줄 = 한 `StructureObservationRecord`. 일자는 **New York 거래일**(설계 §16) |
| 거래 | `App_Data/simtrades.json` | `SimTrade.Structure` = 진입 시점 동결 `FrozenStructureContext`(설계 §10/§11) |

관측 레코드에서 검증이 읽는 필드:
`observationId`, `recordVersion`, `symbol`, `observedAt`, `sessionStart`, `analysisAsOf`, `quoteAt`,
`lastCompletedBarStart`, `engineVersion`, `policyHash`, `mode`, `detail`, `status`, `trend.state`, `warnings`,
그리고 후보별 `eventId`, `kind`, `zoneId`, `state`, `entryQuality`, `triggerBarStart`, `triggerConfirmedAt`,
`expiresAt`, `rejectionCodes`, `notes`, `plan.{missingLiquidity, validSpread, eligibilityCostModelVersion,
realizedFillCostModelVersion, netR}`.

#### `trend.components` (#146)

`detail = "full"` 레코드에서만 `trend.components`가 실린다. `TrendEvaluator`가 이미 계산한 원값(raw)·변환값
(value, tanh 등)을 그대로 노출하며 관측 크기를 줄이려고 `summary`·전이(`transition`) 레코드에서는 생략한다(§16,
이슈 #44 캡 영향 없음).

| 필드 | 의미 | 결측 |
|---|---|---|
| `emaDirection.{raw, value}` | (ema9-ema21)/atr1m, tanh 변환값 | atr1m 또는 ema 결측이면 둘 다 null |
| `slopeDirection.{raw, value}` | ema21 lookback 기울기/atr1m, tanh 변환값 | 위와 동일 |
| `vwapDirection.{raw, value}` | (종가-vwap)/max(vwapSd, atr1m, floor), tanh 변환값 | vwap 결측이면 둘 다 null |
| `structureDirection.{raw, value}` | 확정 5분 피벗 delta의 tanh 평균 (raw는 항상 null, deltaHigh/deltaLow가 원값을 보존) | **5분 구조 자체가 결측이면(`trend.structureEvidenceMissing`) 필드 전체가 null** |
| `efficiency.{raw, value}` | §16B 효율성(raw=value, 변환 없음) | 봉 부족이면 null |

#### `confluence` (#167)

`detail = "full"` 레코드에서만 `confluence`가 실린다. 컨플루언스 층(설계 C1)은 v5 구조 판정과 **분리된 별도
층**이며 1단계에서는 관측·표시 전용이다 — 후보·계획·진입·게이트 어느 경로에도 입력되지 않는다. 진입 경로
소스가 이 층을 참조하지 않는다는 사실은 소스 스캔 테스트(`ConfluencePolicyTests`)로 고정되어 있다.
`summary`·전이(`transition`) 레코드에서는 생략한다(§16, 이슈 #44 캡 영향 없음).

| 필드 | 의미 | 결측 |
|---|---|---|
| `barEnd` | 점수를 만든 완료 1분봉의 끝 시각 | — |
| `score` | C4 합산 `Σ w·c·s / Σ w·c`, [−1,+1] | 기여 기법이 하나도 없으면 **null**(0으로 대체하지 않는다) |
| `warmupCount` | warmup 상태인 기법 수 | — |
| `policyHash` | `ConfluencePolicy`의 canonical JSON SHA-256. `StructurePolicy.policyHash`와 **별개** | — |
| `weightsVersion` | 가중치 집합의 버전. 파일이 없으면 `uniform.1`(전부 1.0, 미검증), K4 측정 파일이 있으면 `w-<yyyyMMdd>-<hash8>`(§5B) | — |
| `techniques[]` | 기법별 `{name, score, confidence, weight, warmup, contributing, correlationGroup, evidence}` | 기법 자체는 항상 10개가 실린다 |

기법별 `score`∈[−1,+1](롱 전용이므로 음수는 "롱에 불리")과 `confidence`∈[0,1] 정의는 설계 C3 표 그대로다.

| 기법(`name`) | score | confidence |
|---|---|---|
| `MACD` | Hist를 ATR로 정규화 후 tanh, Signal 상향 교차 봉은 0선 위일 때만 +0.2 | 0선 위 1.0 / 아래 0.5 |
| `RSI` | (RSI−50)/50 | ADX≥20이면 1.0 / 아니면 0.7 |
| `BB_PERCENT_B` | (%B−0.5)×2 클램프, 직전 봉이 밴드폭 20봉 최저이고 종가가 상단을 이탈하면 +0.3 | 밴드폭이 20봉 최저·최고면 0.6 / 아니면 1.0 |
| `ADX_DMI` | sign(+DI−−DI)×min(ADX/50,1) | ADX≥20이면 1.0 / 아니면 0.4 |
| `VWAP_DEVIATION` | tanh((종가−VWAP)/σ) | 1.0 |
| `RVOL` | tanh(RVOL20−1)×sign(종가−세션 시가) | 20봉 표본이 있으면 1.0 / 없으면 **0** |
| `ATR_CHANNEL` | (종가−EMA20)/(2·ATR) 클램프 | 1.0 |
| `ORB15` | 개장 15분 레인지 상단 대비 위치(위 +, 안 0, 아래 −), 레인지 폭으로 스케일 | 1.0 |
| `RS_QQQ` | tanh((당일 수익률 − QQQ 당일 수익률)/ATR%) | 벤치마크 봉이 ±60초 동기면 1.0 / 아니면 **0** |
| `OBI` | (Bid−Ask)/(Bid+Ask) 최근 3 poll 평균 | 호가가 있으면 1.0 / 결측이면 **0** |

`warmup`이거나 `confidence = 0`인 기법은 합산의 분자·분모 양쪽에서 빠진다. 상관군(`oscillator`,
`volatilityBand`, `range`)은 군 안의 기여 기법 수 n으로 가중치를 1/n 한다 — 1군 목록에는 각 군에 한 기법씩만
있어 현재 실질 계수는 1.0이다. 가중치는 전부 1.0(미검증)에서 시작하며 K4 측정(§5B)이 `verified`로 판정한 기법만 바뀐다(C1 §16A).

조회 API는 두 곳이다. `GET /api/confluence/{symbol}`은 최신 점수와 기법별 값을, `/api/structure/{symbol}`은
additive `confluence: {score, warmupCount, weightsVersion}` 요약을 돌려준다. 두 경로 모두 **조회가 계산을
유발하지 않는다** — 마지막 완료 봉 평가에서 캐시된 값만 읽는다.

### 2.1.1 재진입 코호트 태그 (#111)

`SimTrade.Structure.reentry`는 **진입 시점에 직전 거래를 아는 계층**(`StructuralSimulation.Enter`)이 채우는
관측용 태그다. 진입·청산 판정에 쓰이지 않으며, 이 태그를 근거로 정책을 바꾸지 않는다 —
B-1(동일 targetZoneId 세션 내 소비)·#47 쿨다운 키 kind 제거 판단의 **입력 데이터**일 뿐이다.

| 필드 | 형 | 의미 |
|---|---|---|
| `reentry.sameSymbolWithinBars` | `int?` | 같은 심볼 직전 거래의 **청산 봉을 0번째**로 세어 이번 진입 트리거 봉까지 닫힌 완료 봉 수. #117 손절 쿨다운과 같은 규칙(`BarCounting.CompletedBarsSince`)이다 |
| `reentry.prevExitStatus` | `string?` | 직전 거래의 청산 사유(STOP / TARGET / EOD / CUT …) |
| `reentry.sameTargetZone` | `bool?` | 직전 거래와 `PlanSnapshot.TargetZoneId`가 같은 lineage인지. 진입 계획의 zone `Aliases`(병합으로 흡수된 ID)까지 포함해 비교하고, 직전 거래에 동결 계획이 없으면 `null` |
| `reentry.prevEntryQuality` | `double?` | 직전 진입의 `EntryQualityAtEntry` |
| `reentry.prevTrend` | `string?` | 직전 진입의 `TrendAtEntry` |

결측 규칙(§3.2)을 그대로 따른다.

- `reentry` 객체 자체가 없으면 **태그 도입 이전 데이터**(미수집)다. "첫 진입"으로 바꾸지 않는다.
- **첫 진입**은 `reentry` 객체가 있고 다섯 값이 모두 `null`이다.
- 완료 봉 근거가 없거나 직전 청산이 이전 세션이면 `sameSymbolWithinBars`만 `null`이고 나머지 태그는 남는다.

`/api/sim`의 `structure.groups`는 이 태그를 세 차원으로 분리한다.

| 차원 | 집단 |
|---|---|
| `reentry` | `FIRST_ENTRY` · `R0_2` · `R3_5` · `R6_PLUS` · `REENTRY_BARS_UNCOLLECTED` · `REENTRY_UNTAGGED` |
| `reentryTargetZone` | `FIRST_ENTRY` · `SAME_TARGET_ZONE` · `OTHER_TARGET_ZONE` · `TARGET_ZONE_UNCOLLECTED` · `REENTRY_UNTAGGED` |
| `reentryPrevExit` | `FIRST_ENTRY` · `PREV_STOP` · `PREV_TARGET` · `PREV_EOD` · `PREV_CUT` · … · `REENTRY_UNTAGGED` |

미수집 집단은 `collected=false`로 나가며 0건 성과로 읽지 않는다.

### 2.2 연결 키

```
(Symbol, SessionStart, EventId, EngineVersion, PolicyHash, Mode)
```

- **`SessionStart`는 관측에서만 온다.** `SimTrade`는 세션 시작을 저장하지 않으므로 달력으로 추측하지 않는다.
- **`Mode`(shadow/active)는 키의 일부다.** 관측 전용 실행과 실제 진입 실행은 성격이 다른 집단이라 합치지 않는다.
- 거래는 `FrozenStructureContext.EntryEventId`로 후보에 붙는다. 종목 또는 엔진 버전/정책 해시가 다르면
  **연결하지 않고 충돌로 기록**한다(`TRADE_SYMBOL_MISMATCH`, `TRADE_VERSION_MISMATCH`).

### 2.3 상태 체인

```
후보(WAIT) → 결정(READY / REJECTED / INVALIDATED / EXPIRED) → 진입(ENTERED) → 거래 → 결과(TARGET/STOP/EOD/...)
```

`CandidateOutcome`은 최종 관측의 `state`에서 오고, 알 수 없는 문자열은 `Unknown`으로 남긴다(추측하지 않는다).

---

## 3. 중복 · 결측 · 시각 · 보존 한계

### 3.1 중복 제거 (필수)

| 상황 | 처리 |
|---|---|
| 같은 `eventId`를 15초마다 다시 관측 | **한 표본으로 접는다.** `observationCount`/`pollRowsCollapsed`로만 남는다 |
| 최종 상태 선택 | `analysisAsOf` → `observedAt` → `observationId` ordinal 순으로 가장 나중 관측 |
| 재시작으로 같은 `observationId`가 두 번 append | 첫 줄만 사용, `DUPLICATE_OBSERVATION_ID` 기록 |
| 한 `eventId`가 두 엔진 버전/정책 해시/모드/종목에 걸침 | **평가에서 통째로 제외**(`droppedEvents`) + `EVENT_*_MIXED` 충돌 |

같은 이벤트의 반복 poll을 독립 표본으로 세면 표본 수가 부풀고 승률이 왜곡된다. 이 규칙은 회귀 테스트로 고정되어 있다.

### 3.2 결측 — 0이나 false로 바꾸지 않는다

| 필드 | 결측이 뜻하는 것 |
|---|---|
| `entryQuality = null` | 필수 구성요소가 없어 점수를 만들 수 없었다(설계 §16A). READY가 될 수 없었던 후보다 |
| `plan = null` (거절·대기 후보) | **비용 가정이 저장되지 않는다.** 비용 코호트 분리는 계획이 성립한 건에서만 가능하다 |
| `missingLiquidity = true` | 호가가 없어 **spread=0으로 가정**하고 자격을 평가했다(현 운영 정책, 이 이슈에서 바꾸지 않는다) |
| `trend.state` 없음 | 추세 미수집. 추세 코호트에서 별도 집단으로 분리된다 |
| `detail = "summary"` | Zone 배열·품질 상세가 없다(§16). 근거 재현 범위가 좁다 |
| `WidthFromTickOnly` | 1.0.2609.1201부터 가격선 반폭 생성 시 ATR이 없어서 tick 하한만 쓴 경우를 뜻한다. 일봉 context level은 세션 최초 사용 가능 ATR이 생기면 그 ATR 폭으로 재계산되며 이 플래그를 붙이지 않는다 |

호가는 **결측 / 관측된 0 스프레드 / 관측된 양수 스프레드** 세 집단으로 나눈다. 결측과 "실제로 0"을 합치면
비용 가정의 효과가 보이지 않는다.

### 3.3 §6.3 C-2 daily 폭 예외

C-2. 일봉 context level은 세션 시작 전 이미 알려진 원천이지만, 장 초반에는 1분 ATR14가 아직 없다. ATR이 없을 때는 tick 하한 반폭과 `WidthFromTickOnly`로 노출하고, 세션 최초 사용 가능 ATR이 생긴 뒤에는 `0.15·ATR_first` 반폭으로 확장해 `WidthFromTickOnly`를 제거한다. 병합 간격과 `maxWidth`는 여전히 cutoff ATR 기준이며, 확장된 daily zone과 인접 pivot zone의 합산 폭이 `maxWidth`를 넘으면 병합하지 않는다.

### 3.4 시간 규율

- `observedAt` 또는 `analysisAsOf`가 `AsOf`보다 뒤인 관측은 **입력에서 제외**한다.
- `enteredAt`이 `AsOf`보다 뒤인 거래는 **연결하지 않는다**(그 시점에는 진입하지 않은 것이다).
- `exitAt`이 `AsOf`보다 뒤인 거래는 **결과 미확정으로 절단**한다: `statusAsOf="OPEN"`, `pnlPercent=null`,
  `outcomeKnown=false`. 저장된 값은 `storedStatus`에 남지만 어떤 평균에도 들어가지 않는다.
- 학습/검증 분리는 **New York 거래일 단위 anchored walk-forward**다. 한 세션이 두 구간에 동시에 들어가지 않고,
  검증 구간의 모든 거래일은 학습 구간보다 뒤다. 세션이 `folds+1`개 미만이면 fold를 만들지 않고 검증 불가로 보고한다.

### 3.5 보존 한계 (표본이 좋아 보여도 사라지지 않는다)

| 한계 코드 | 내용 |
|---|---|
| `OBSERVATION_RETENTION_CAPPED` | 관측 파일은 거래일당 20 MiB에서 append가 멈춘다(§16). 상한에 닿은 날은 **후보 전수가 아니다** |
| `TRADE_RETENTION_CAPPED_500` | 거래 저장소는 최신 500건만 보존한다(`SimulationEngine`). 거래 부재가 "진입한 적 없음"의 증거가 아니다 |
| `SUB_BAR_STATE_NOT_RETAINED` | 관측은 완료 봉 단위다. poll 사이의 중간 상태는 복원할 수 없다 |
| `SUMMARY_OBSERVATION_OMITS_EVIDENCE` | 요약 관측에는 Zone·품질 상세가 없다 |
| `COST_ASSUMPTION_ONLY_FOR_PLANNED_CANDIDATES` | 계획 없는 후보의 비용 가정은 수집되지 않는다 |
| `REJECTED_PATH_NOT_RETAINED` | **거절 후보의 사후 가격 경로가 없다.** 거절 필터의 효과를 직접 측정할 수 없다(선택 편향 잔존) |
| `TRADE_WITHOUT_OBSERVATION:<id>` | 근거 후보를 찾지 못한 거래. 관측 보존 한계의 직접 증거다 |

---

## 4. 평가 체계 (사전 정의)

### 4.1 코호트 차원 — 코드에 고정되어 있고 데이터에 맞춰 바꾸지 않는다

| 차원 | 집단 |
|---|---|
| `decision` | ENTERED / READY / WAIT / REJECTED / INVALIDATED / EXPIRED |
| `entryQuality` | `Q0_25` · `Q25_50` · `Q50_75` · `Q75_100` · 미수집 — **이슈 #27 `SimulationCohorts`와 같은 경계** |
| `setup` | PULLBACK / BREAKOUT / REBOUND |
| `trend` | UP / TRANSITION / RANGE / DOWN / 판정 불가 |
| `liquidity` | 관측 스프레드 / 관측 0 스프레드 / 결측(0 가정) / 미수집 |
| `exitEstimation` | 확정 청산 / 추정 청산 / 청산 없음 |
| `costModel` | 자격 비용 모델 / 실현 체결 비용 모델 |
| `policyVersion` | 엔진 버전 / 정책 해시 |
| `mode` | shadow / active |

진입 품질 구간은 순위 지표의 중립 구간이며 "좋음/나쁨" 라벨을 붙이지 않는다(설계 §9.4).

### 4.2 각 코호트가 보고하는 값

- **규모·기간·집중도**: 후보 수, 진입 수, 청산 수, 실현 손익 유효 건수, 손익 결측, 절단된 건, 추정 청산 건,
  종목 수, 세션 수, 첫/마지막 세션, 최다 종목과 그 비중(%).
- **비용 차감 후 손익(%)**: 평균 · 중앙값 · 표준편차 · 표준오차 · **95% 정규근사 구간** · 최소 · 최대 · 합.
- **관측 승률(%)**: 분모는 실현 손익이 유효한 청산 건이다. 표본이 없으면 `null`이며 0%가 아니다.
- **계획 netR 평균**: 동결 계획의 계획값이다. 실현 손익과 **절대 합산하지 않는다**.
- **상위 거절 사유**: 선택 편향을 눈으로 볼 수 있게 분포로 남긴다.

### 4.2.1 진입 차단 사유 (#111)

`evaluation.entryBlocks`는 READY 후보가 진입으로 이어지지 않은 사유를 **코드별로 따로** 센다.
두 코드는 성격이 달라 한 숫자로 합치지 않는다.

| 코드 | 뜻 |
|---|---|
| `V5_ENTRY_SUPPRESSED_BY_SAME_POLL_EXIT` | #106 — 같은 poll에 청산이 있어 이번 poll의 진입만 건너뛴 건. 쿨다운이 아니라 **1 poll 지연**이다 |
| `V5_ENTRY_BLOCKED_BY_STOP_COOLDOWN` | #117 — 직전 손절 이후 완료 봉이 정책 개수만큼 쌓이지 않아 **차단**된 건 |

`events`는 이벤트 기준(같은 이벤트의 반복 poll은 한 건), `symbols`는 그 이벤트가 걸친 종목 수다.
근거는 후보의 `rejectionCodes`(#107)이며 0건도 그대로 보고한다.

### 4.3 표본 충분성 — "검증 불가"를 통과로 위장하지 않는다

기본 임계값(`EvaluationThresholds.Default`, **사전 정의**):

| 기준 | 값 |
|---|---|
| 실현 손익 유효 거래 | ≥ 20건 |
| 세션 수 | ≥ 3 |
| 종목 수 | ≥ 3 |
| 최다 종목 비중 | ≤ 60% |

| 결론 | 의미 |
|---|---|
| `NotCollected` | 값 자체가 수집되지 않은 코호트(미수집). 0건과 다르다 |
| `InsufficientSample` | **검증 불가.** 기준 미달 사유를 코드로 함께 낸다 |
| `Observed` | 기준을 넘은 관측 결과. **승률 보장이 아니다** |

기준 미달이어도 관측 수치는 그대로 보고한다(숨기지 않는다). 다만 결론은 "검증 불가"다.

### 4.4 점수 구간의 구분력

`qualityBands`는 각 구간의 평균 손익과 95% 구간을 낸다. 결론 규칙:

- 비교 가능한 구간(각 ≥ 실현 거래 임계값)이 2개 미만이면 `InsufficientSample`.
- 2개 이상이면 구간 평균의 폭(`meanSpreadPercent`), 단조성(`monotonic`), **끝 구간 95% 구간의 분리 여부**
  (`intervalsSeparated`)를 보고한다.
- 구간이 겹치면 "구분된다"고 말하지 않는다. 재현은 독립 기간(walk-forward 검증 구간)에서 확인한다.

### 4.5 거절 후보의 가상 평가 (필드 분리)

`rejected.hypothetical`은 **가상 평가**이며 실제 체결 성과와 절대 합산하지 않는다.
관측 파일이 결정 이후 가격 경로를 보존하지 않으므로 기본값은 `collected=false`, `basis="NOT_RETAINED"`다.
호출자가 과거 확정 봉으로 재생한 결과를 넣으면 `basis="CALLER_SUPPLIED_REPLAY"`로 **별도 필드에만** 채워진다.

### 4.6 비용 민감도 (오프라인 시나리오)

`costScenarios`는 사전 정의된 세 가지다.

| 이름 | 적용 대상 | 추가 왕복 비용 |
|---|---|---|
| `missing-liquidity-10bps` | 호가 결측 건만 | 0.10%p |
| `missing-liquidity-25bps` | 호가 결측 건만 | 0.25%p |
| `all-trades-25bps` | 모든 진입 | 0.25%p |

`realizedMeanPercent`(원본)와 `scenarioMeanPercent`(가정)는 **다른 필드**다. 시나리오가 저장된 실현 손익을
덮어쓰지 않으며 운영 비용 모델도 바꾸지 않는다.

---

## 5. 재현 절차

```sh
# 1) 서버 기동 후 (또는 CI에서 테스트로)
curl -s 'http://127.0.0.1:5188/api/validation?days=30' > validation.json

# 2) 데이터 감사부터 읽는다 — 결론보다 먼저 한계를 본다
#    data.filesFound / data.daysWithoutFile / data.parseFailures / data.fieldGaps
#    data.tradeStoreAtLimit / data.files[].nearDailyLimit
#    link.conflicts / link.limitations

# 3) 결론을 읽는다
#    evaluation.overall.verdict 가 InsufficientSample 이면 거기서 멈춘다(검증 불가)
#    evaluation.groups[*].cohorts[*].verdict 를 코호트별로 같은 규칙으로 읽는다
#    evaluation.qualityBands.intervalsSeparated 가 true 가 아니면 "구간이 성과를 구분한다"고 말하지 않는다

# 4) 재현성 확인
#    walkForward.result[*].outOfSample 이 inSample 과 같은 방향인지 본다
#    한 fold에서만 좋은 결과는 근거가 아니다

# 5) 비용 가정에 민감한지 본다
#    costScenarios[*].scenarioMeanPercent 가 부호를 바꾸면 결론은 비용 가정에 의존한다
```

로컬 검증:

```sh
dotnet build server/Astra.Server.csproj -c Release
dotnet test server/tests/Astra.Server.Tests.csproj -c Release
node .github/scripts/repository-policy.mjs
```

### 결정성

같은 입력이면 같은 결과가 나온다: 정렬 기준(세션 → 종목 → EventId), 대표 거절 사유(ordinal 최소),
반올림(소수 4자리), walk-forward 분할(정렬된 거래일의 연속 블록)이 모두 고정되어 있다.
회귀 테스트는 `server/tests/ValidationLinkerTests.cs`, `ValidationEvaluationTests.cs`, `ValidationServiceTests.cs`에 있고
fixture는 전부 코드로 생성한다(**운영 실데이터는 커밋하지 않는다**).

---

## 5A. 실매매 대조 (#131)

사용자의 실계좌 체결은 v5 밖에서 만들어진 **외부 기준**이다. 엔진 성과의 증거가 아니라
"엔진이 내 매매와 어디서 갈렸는가"를 재는 축으로만 쓴다.

### 5A.1 수집 — 읽기 전용

`GET /orders?status=CLOSED`만 호출한다. **주문 생성·정정·취소 API는 어떤 경로에서도 호출하지 않는다.**
세션 종료(종료 시각 경과) 후 그 거래일에 대해 자동 1회, 그리고
`POST /api/validation/real-fills/refresh?date=YYYY-MM-DD`로 수동 수집한다.
커서로 전량 순회하며(페이지 100건, ORDER_HISTORY 5/s를 넘지 않도록 페이지 간 간격),
`status=FILLED`이고 체결 정보가 있는 주문만 남긴다. 실패는 진단 로그로만 남고 진입·청산을 막지 않는다.

### 5A.2 저장 계약 — `App_Data/real-fills/YYYY-MM-DD.json`

| 필드 | 의미 |
|---|---|
| `recordVersion` | `real-fills.1` |
| `tradingDate` | New York 거래일 |
| `collectedAt` | 수집 시각 |
| `fills[].symbol` | 종목 |
| `fills[].side` | `BUY` / `SELL` |
| `fills[].filledAt` | 체결 시각 |
| `fills[].averageFilledPrice` | 평균 체결가 |
| `fills[].filledQuantity` | 체결 수량 |
| `fills[].commission` | 수수료 |
| `fills[].orderType` | 주문 유형 |

**저장하지 않는 것**: 계좌번호(`accountSeq`)·주문 식별자(`orderId`)·체결 금액 합계(`filledAmount`)·잔고.
게이트웨이가 이 필드를 채워 와도 저장 단계에서 떨어진다(`RealFillsServiceTests`가 고정한다).
`App_Data/`는 gitignore 대상이라 실데이터는 저장소에 들어가지 않는다.

### 5A.3 대조 규칙 (`RealFillComparer`, Domain 순수 함수)

- **창**: 같은 심볼, 체결 시각 ±5분. 경계는 **양끝 포함**이다.
- **MATCHED**: 실매수 창 안에 v5 `READY` 또는 `ENTERED` 후보가 있었다. 대표 후보는 `ENTERED` → `READY` 순,
  같은 상태면 시각이 가까운 것, 그래도 같으면 `eventId` ordinal 최소.
- **USER_ONLY**: 실매수 창 안에 후보가 없거나 `REJECTED`뿐이다. 대표가 `REJECTED`면 그 거절 코드를 함께 남긴다.
- **ENGINE_ONLY**: v5 `ENTERED` 이벤트인데 같은 창에 실매수가 없다. 같은 `eventId`의 반복 관측은 첫 관측 한 건으로 접힌다.
- **가격 괴리**: `(체결가 − entryReference) / atr1m`. ATR이 없거나 0 이하면 **null**이며 0으로 바꾸지 않는다.
- **실매도**: 그 시점 열려 있던 v5 거래(`enteredAt ≤ 체결시각 ≤ exitAt`, 열린 거래는 `exitAt` 없음)의
  `ABOVE_TARGET` / `BETWEEN` / `BELOW_STOP` 위치만 본다. 열린 거래가 없으면 null이다.
- 매칭률의 분모는 **실매수 건수**다. 실매도는 분모에 들어가지 않으며, 실매수가 0건이면 매칭률은 null이다(0%가 아니다).

### 5A.4 조회 — `GET /api/validation/real-vs-v5?date=YYYY-MM-DD`

조회는 Toss를 호출하지 않는다. 저장된 실체결 파일·그날 관측 jsonl(#28과 같은 리더)·`simtrades.json`만 읽는다.
응답은 `tradingDate`, `fillsCollected`, `collectedAt`, `observationLines`, `observationFileFound`,
`report`(매칭률·세 분류 건수·괴리 중앙값/사분위·`topUserOnlyRejections` 상위 5개·`rows`), `limitations`다.
`limitations`의 `REAL_FILLS_NOT_COLLECTED`(실체결 미수집)·`REAL_FILLS_NO_OBSERVATIONS`(그날 관측 파일 없음)는
"대조 불가"를 뜻하며 0건 성과로 읽지 않는다.

---

## 5B. 컨플루언스 측정 파이프라인 (#169, 설계 C5)

정교함을 **측정**하는 유일한 경로다. 가중치는 이 결과로만 바뀐다. 진입·게이트는 이 절의 영향을 받지 않는다.

### 5B.1 실행

```bash
# 기본 창: 오늘을 적용 주 시작일로 보는 직전 4주 (측정 4주 → 적용 1주 워크포워드)
dotnet run --project server -- confluence-measure
# 창·지평 지정
dotnet run --project server -- confluence-measure --from 2026-08-17 --to 2026-09-13 --horizon 10
```

서버를 띄우지 않고 `App_Data/bars`만 읽어 표를 출력하고 `App_Data/confluence-weights.json`을 쓴 뒤 종료한다.
저장 봉이 한 개도 없으면 **파일을 쓰지 않는다**(전부 1.0 유지). 외부 API를 호출하지 않는다.

### 5B.2 측정 정의

- **재생**: 날짜 폴더·심볼별로 저장 완료 1분봉을 시간순 재생한다. 각 창은 0..i 슬라이스만 담은 새 객체이며
  현재 봉 이후는 창 안에 **존재하지 않는다** — 미래 차단은 타입과 테스트로 고정되어 있다(C6).
- **신호 발생**: warmup이 아니고 `confidence > 0`이며 `|score| ≥ 0.3`. 이 임계는 `MeasurementPolicy`의
  상수이며 **성과로 탐색한 값이 아니다(미검증)**.
- **결과**: 신호 봉 종가 기준 N봉 후(5·10·20) 종가 변화 ÷ ATR14. 방향 적중은 `sign(score) == sign(수익)`이고
  움직임이 0이면 적중이 아니다. 기대값은 왕복 0.2% + 스프레드 0.01%를 뺀 값이며, **저장 호가가 없어
  스프레드는 정책 기본값**이다(관측값이 아니다). 남은 봉이 N개 미만이면 표본에서 빠진다.
- **결측**: 저장 호가 스냅샷이 없으므로 `OBI`는 항상 c=0(표본 0)이고, 그날 QQQ 봉이 저장돼 있지 않으면
  `RS_QQQ`도 c=0이다. 전일 종가를 저장하지 않으므로 세션 첫 봉 TR은 고저폭으로 계산한다.

### 5B.3 통계

| 값 | 정의 |
|---|---|
| `n` | 지평별 신호 표본 수 |
| `hitRate` | 적중 / n |
| `ci` | Wilson score interval 95% — `(p̂ + z²/2n ± z·√(p̂(1−p̂)/n + z²/4n²)) / (1 + z²/n)`, z=1.959964 |
| `brier` | `(1/n)·Σ(f−o)²`, 예측확률 `f = (score+1)/2`, 결과 `o = 적중 1 / 미적중 0` |
| `pValue` | 귀무가설 적중률 0.5의 이항 정확검정 양측 p — `min(2·P(X≤k), 2·P(X≥k), 1)` |
| BH 보정 | 기법 10개 동시, q=0.05. p를 오름차순으로 두고 `p(k) ≤ (k/m)·q`를 만족하는 최대 k까지 기각 |

`status`는 `unverified`(n<50) · `rejected`(n≥50이지만 BH 미통과) · `verified`(n≥50 ∧ BH 통과)다.
**표본 부족은 통과로 위장하지 않는다** — 가중치가 바뀌지 않는다는 뜻이다.

### 5B.4 재가중과 파일 스키마

`w = clamp((적중률 − 0.5) × 2, 0, 1)`이며 **`verified`일 때만** 적용한다. `unverified`·`rejected`는 1.0을
유지한다. 워크포워드는 측정 4주 → 적용 1주이며 측정 창 밖 날짜 폴더는 읽지 않는다.

```json
{
  "weightsVersion": "w-20260914-1a2b3c4d",
  "measuredAt": "2026-09-14T06:00:00+00:00",
  "window": { "from": "2026-08-17", "to": "2026-09-13" },
  "horizonBars": 10,
  "weights": {
    "MACD": { "w": 0.4, "n": 120, "hitRate": 0.7, "ci": [0.6042, 0.781], "brier": 0.22,
              "pValue": 0.0001, "status": "verified" }
  }
}
```

`weightsVersion`은 `w-<측정일 yyyyMMdd>-<결과 SHA-256 앞 8자>`다 — 같은 측정 결과는 같은 버전을 낸다.
`brier`·`pValue`·`ci`는 표본이 없으면 `null`이다.

### 5B.5 서버 로딩과 조회

서버는 기동 시 이 파일을 **한 번** 읽어 K2 합산과 관측 `weightsVersion`에 주입한다. 파일이 없거나 깨졌으면
무시하고 경고 로그를 남긴 뒤 전부 1.0(`uniform.1`)으로 돈다. 현재 가중치와 근거는
`GET /api/confluence/weights`가 `{weightsVersion, source, measuredAt, window, horizonBars, weights}`로
돌려준다. `source`는 파일을 읽었으면 `file`, 기본값이면 `default`다. 조회가 측정을 유발하지 않는다.

---

## 6. 하지 않는 것

- 운영 진입/청산·점수 공식·호가 결측 정책 변경 — 이 경로는 읽기 전용이다.
- 수익이 좋게 나오는 임계값 탐색·자동 조정. 구간·차원·임계값은 코드에 고정되어 있다.
- 고정 10건 개선 반복 자동화.
- 승률 보장 표현. 모든 수치는 관측이며 다음 세션의 재현을 약속하지 않는다.
- 표본 부족을 0이나 통과로 표시하는 것.
