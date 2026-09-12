"""C2(#166) 지표 참조 벡터 생성기.

실행: python server/tests/Indicators/vectors/generate.py
출력: 같은 폴더의 *.json (입력 봉과 기대 출력을 함께 저장)

참조 소스
  EMA/MACD/RSI/ATR/BBANDS/ADX/PLUS_DI/MINUS_DI/STOCH/MAX/MIN : TA-Lib 파이썬 래퍼
  VWAP / 5m·15m 집계 / 틱룰 : TA-Lib에 없어 이 파일의 독립 수기 계산식 사용

MACD 주의: talib.MACD는 fast EMA 시딩 구간을 signal lookback만큼 밀어 계산해
C2 채택 정의(각 EMA를 자기 첫 n봉 SMA로 시딩)와 다르다. 따라서 MACD 라인은
talib.EMA(12) - talib.EMA(26), Signal은 그 MACD 라인에 talib.EMA(9)를 적용해 합성한다.
"""
import json
import os
from decimal import Decimal

import numpy as np
import talib

HERE = os.path.dirname(os.path.abspath(__file__))
SESSION_OPEN = "2026-09-11T09:30:00-04:00"


def bar_times(count, start_minute=0):
    base = np.datetime64("2026-09-11T13:30:00")
    return [
        (
            str(base + np.timedelta64(start_minute + i, "m")) + "Z",
            str(base + np.timedelta64(start_minute + i + 1, "m")) + "Z",
        )
        for i in range(count)
    ]


def random_walk_bars(seed, count, start=100.0, sigma=0.35, gap_at=None, doji_at=None):
    rng = np.random.default_rng(seed)
    closes = []
    price = start
    for i in range(count):
        price += rng.normal(0, sigma)
        if gap_at is not None and i == gap_at:
            price += 3.5
        closes.append(price)

    bars = []
    times = bar_times(count)
    prev_close = start
    for i, close in enumerate(closes):
        open_ = prev_close
        spread = abs(rng.normal(0, sigma)) + 0.05
        high = max(open_, close) + spread
        low = min(open_, close) - spread
        volume = float(int(rng.integers(800, 5000)))
        if doji_at is not None and i == doji_at:
            open_ = high = low = close = round(close, 4)
            volume = float(int(rng.integers(800, 5000)))
        bars.append(
            {
                "start": times[i][0],
                "end": times[i][1],
                "open": round(float(open_), 4),
                "high": round(float(high), 4),
                "low": round(float(low), 4),
                "close": round(float(close), 4),
                "volume": volume,
            }
        )
        prev_close = close
    return bars


def flat_bars(count, price=50.0, volume=1000.0):
    times = bar_times(count)
    return [
        {
            "start": times[i][0],
            "end": times[i][1],
            "open": price,
            "high": price,
            "low": price,
            "close": price,
            "volume": volume,
        }
        for i in range(count)
    ]


def arrays(bars):
    return (
        np.array([b["open"] for b in bars], dtype=float),
        np.array([b["high"] for b in bars], dtype=float),
        np.array([b["low"] for b in bars], dtype=float),
        np.array([b["close"] for b in bars], dtype=float),
        np.array([b["volume"] for b in bars], dtype=float),
    )


def nullable(values):
    return [None if v is None or not np.isfinite(v) else round(float(v), 10) for v in values]


def ema_reference(source, period):
    values = talib.EMA(np.asarray(source, dtype=float), timeperiod=period)
    return list(values)


def write(name, payload):
    path = os.path.join(HERE, name)
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=1)
        handle.write("\n")
    print("wrote", name)


def macd_reference(close):
    fast = talib.EMA(close, timeperiod=12)
    slow = talib.EMA(close, timeperiod=26)
    macd = fast - slow
    compact = np.array([v for v in macd if np.isfinite(v)], dtype=float)
    signal_compact = talib.EMA(compact, timeperiod=9)
    signal = np.full(len(close), np.nan)
    offset = len(close) - len(compact)
    signal[offset:] = signal_compact
    hist = macd - signal
    return macd, signal, hist


def vwap_reference(high, low, close, volume):
    typical = (high + low + close) / 3.0
    vwap, sigma = [], []
    cum_v = 0.0
    cum_pv = 0.0
    cum_pv2 = 0.0
    for i in range(len(close)):
        cum_v += volume[i]
        cum_pv += typical[i] * volume[i]
        cum_pv2 += typical[i] * typical[i] * volume[i]
        if cum_v <= 0:
            vwap.append(np.nan)
            sigma.append(np.nan)
            continue
        mean = cum_pv / cum_v
        var = cum_pv2 / cum_v - mean * mean
        if var < 1e-14:
            var = 0.0
        vwap.append(mean)
        sigma.append(np.sqrt(var))
    return np.array(vwap), np.array(sigma)


def aggregate_reference(bars, slots):
    buckets = {}
    for index, bar in enumerate(bars):
        bucket = index // slots
        buckets.setdefault(bucket, []).append(bar)
    out = []
    for bucket in sorted(buckets):
        members = buckets[bucket]
        if len(members) != slots:
            continue
        out.append(
            {
                "start": members[0]["start"],
                "end": members[-1]["end"],
                "open": members[0]["open"],
                "high": max(m["high"] for m in members),
                "low": min(m["low"] for m in members),
                "close": members[-1]["close"],
                "volume": sum(m["volume"] for m in members),
            }
        )
    return out


def tick_reference(prices):
    threshold = Decimal("0.005")
    out = []
    previous = None
    for raw in prices:
        price = Decimal(str(raw))
        if previous is None:
            out.append("Undetermined")
        elif abs(price - previous) < threshold:
            out.append("Flat")
        elif price > previous:
            out.append("Up")
        else:
            out.append("Down")
        previous = price
    return out


def candle_bars(seed, count):
    """장악형·망치·핀바가 섞이도록 만든 합성 봉. 값은 4자리로 고정해 C#/TA-Lib 입력을 동일하게 둔다."""
    rng = np.random.default_rng(seed)
    times = bar_times(count)
    bars = []
    price = 100.0
    for i in range(count):
        open_ = price
        close = price + rng.normal(0, 0.25)
        spread = abs(rng.normal(0, 0.2)) + 0.05
        high = max(open_, close) + spread
        low = min(open_, close) - spread
        if i % 9 == 4:
            open_ = price - 0.15
            close = price + 0.9
            high = close + 0.05
            low = open_ - 0.05
        if i % 9 == 7:
            open_ = price + 0.35
            close = price + 0.40
            high = close + 0.02
            low = price - 1.4
        if i % 9 == 1 and bars:
            bottom = bars[-1]["low"] - 0.02
            open_ = bottom
            close = bottom + 0.02
            high = close + 0.01
            low = bottom - 1.1
        bars.append(
            {
                "start": times[i][0],
                "end": times[i][1],
                "open": round(float(open_), 4),
                "high": round(float(high), 4),
                "low": round(float(low), 4),
                "close": round(float(close), 4),
                "volume": float(int(rng.integers(800, 5000))),
            }
        )
        price = bars[-1]["close"]
    return bars


def pinbar_reference(bars):
    out = []
    for bar in bars:
        span = bar["high"] - bar["low"]
        tail = min(bar["open"], bar["close"]) - bar["low"]
        out.append(100 if span > 0 and tail >= span * 2.0 / 3.0 else 0)
    return out


def main():
    normal = random_walk_bars(seed=20260912, count=60, gap_at=31, doji_at=44)
    boundary = flat_bars(30)
    tight = random_walk_bars(seed=7, count=40, start=12.3456, sigma=0.0009)

    for label, bars in (("normal", normal), ("boundary", boundary)):
        _, high, low, close, volume = arrays(bars)

        write(
            f"ema-{label}.json",
            {
                "source": "TA-Lib 0.7.1 EMA(timeperiod=n)",
                "period": 20,
                "bars": bars,
                "expected": nullable(ema_reference(close, 20)),
            },
        )

        macd, signal, hist = macd_reference(close)
        write(
            f"macd-{label}.json",
            {
                "source": "TA-Lib 0.7.1 EMA(12)-EMA(26), Signal=EMA(9) of MACD",
                "fastPeriod": 12,
                "slowPeriod": 26,
                "signalPeriod": 9,
                "bars": bars,
                "expectedMacd": nullable(macd),
                "expectedSignal": nullable(signal),
                "expectedHistogram": nullable(hist),
            },
        )

        write(
            f"rsi-{label}.json",
            {
                "source": "TA-Lib 0.7.1 RSI(timeperiod=14)",
                "period": 14,
                "bars": bars,
                "expected": nullable(talib.RSI(close, timeperiod=14)),
            },
        )

        atr = talib.ATR(high, low, close, timeperiod=14)
        write(
            f"atr-{label}.json",
            {
                "source": "TA-Lib 0.7.1 ATR(timeperiod=14); 첫 봉을 전일 종가로 넘겨 정렬",
                "period": 14,
                "previousSessionClose": bars[0]["close"],
                "bars": bars[1:],
                "expected": nullable(atr[1:]),
            },
        )

        upper, middle, lower = talib.BBANDS(close, timeperiod=20, nbdevup=2, nbdevdn=2, matype=0)
        write(
            f"bbands-{label}.json",
            {
                "source": "TA-Lib 0.7.1 BBANDS(timeperiod=20, nbdevup=2, nbdevdn=2, matype=0 SMA, 모집단 σ)",
                "period": 20,
                "deviations": 2,
                "bars": bars,
                "expectedUpper": nullable(upper),
                "expectedMiddle": nullable(middle),
                "expectedLower": nullable(lower),
            },
        )

        write(
            f"adx-{label}.json",
            {
                "source": "TA-Lib 0.7.1 ADX/PLUS_DI/MINUS_DI(timeperiod=14)",
                "period": 14,
                "bars": bars,
                "expectedPlusDi": nullable(talib.PLUS_DI(high, low, close, timeperiod=14)),
                "expectedMinusDi": nullable(talib.MINUS_DI(high, low, close, timeperiod=14)),
                "expectedAdx": nullable(talib.ADX(high, low, close, timeperiod=14)),
            },
        )

        slowk, slowd = talib.STOCH(
            high, low, close, fastk_period=14, slowk_period=3, slowk_matype=0, slowd_period=3, slowd_matype=0
        )
        write(
            f"stoch-{label}.json",
            {
                "source": "TA-Lib 0.7.1 STOCH(fastk=14, slowk=3 SMA, slowd=3 SMA)",
                "fastKPeriod": 14,
                "slowKPeriod": 3,
                "slowDPeriod": 3,
                "bars": bars,
                "expectedSlowK": nullable(slowk),
                "expectedSlowD": nullable(slowd),
            },
        )

        write(
            f"donchian-{label}.json",
            {
                "source": "TA-Lib 0.7.1 MAX(high,20)/MIN(low,20), 중앙=평균(수기)",
                "period": 20,
                "bars": bars,
                "expectedUpper": nullable(talib.MAX(high, timeperiod=20)),
                "expectedLower": nullable(talib.MIN(low, timeperiod=20)),
            },
        )

        vwap, sigma = vwap_reference(high, low, close, volume)
        write(
            f"vwap-{label}.json",
            {
                "source": "수기 — HLC/3 × V 누적, σ=sqrt(Σv·tp²/Σv − vwap²)",
                "sessionOpen": SESSION_OPEN,
                "bars": bars,
                "expectedVwap": nullable(vwap),
                "expectedStdDev": nullable(sigma),
            },
        )

    _, high, low, close, volume = arrays(tight)
    write(
        "ema-subpenny.json",
        {
            "source": "TA-Lib 0.7.1 EMA(timeperiod=20) — 서브페니 반올림 경계",
            "period": 20,
            "bars": tight,
            "expected": nullable(ema_reference(close, 20)),
        },
    )
    write(
        "bbands-subpenny.json",
        {
            "source": "TA-Lib 0.7.1 BBANDS(20,2,2,SMA) — 서브페니 반올림 경계",
            "period": 20,
            "deviations": 2,
            "bars": tight,
            "expectedUpper": nullable(talib.BBANDS(close, 20, 2, 2, 0)[0]),
            "expectedMiddle": nullable(talib.BBANDS(close, 20, 2, 2, 0)[1]),
            "expectedLower": nullable(talib.BBANDS(close, 20, 2, 2, 0)[2]),
        },
    )

    aggregate_full = random_walk_bars(seed=99, count=30)
    write(
        "aggregate-normal.json",
        {
            "source": "수기 — 개장 기준 경계, 완료 구간만",
            "sessionOpen": SESSION_OPEN,
            "bars": aggregate_full,
            "expected5m": aggregate_reference(aggregate_full, 5),
            "expected15m": aggregate_reference(aggregate_full, 15),
        },
    )
    partial = aggregate_full[:-3]
    write(
        "aggregate-boundary.json",
        {
            "source": "수기 — 마지막 구간이 미완료라 제외됨",
            "sessionOpen": SESSION_OPEN,
            "bars": partial,
            "expected5m": aggregate_reference(partial, 5),
            "expected15m": aggregate_reference(partial, 15),
        },
    )

    for label, bars in (("normal", candle_bars(seed=20260912, count=60)), ("boundary", flat_bars(20))):
        open_, high, low, close, _ = arrays(bars)
        write(
            f"cdl-{label}.json",
            {
                "source": "TA-Lib 0.7.1 CDLENGULFING·CDLHAMMER, 핀바는 수기(꼬리 >= 범위 2/3)",
                "bars": bars,
                "expectedEngulfing": [int(v) for v in talib.CDLENGULFING(open_, high, low, close)],
                "expectedHammer": [int(v) for v in talib.CDLHAMMER(open_, high, low, close)],
                "expectedPinBar": pinbar_reference(bars),
            },
        )


    normal_ticks = [10.0, 10.01, 10.012, 10.005, 9.999, 9.999, 10.5, 10.4999]
    write(
        "tickrule-normal.json",
        {
            "source": "수기 — |Δ| < 0.005 보합, 첫 체결 미정",
            "prices": normal_ticks,
            "expected": tick_reference(normal_ticks),
        },
    )
    edge_ticks = [20.0, 20.0049, 20.0099, 20.0149, 20.0099, 20.0, 20.0, 20.005]
    write(
        "tickrule-boundary.json",
        {
            "source": "수기 — 임계 0.005 직전/직후",
            "prices": edge_ticks,
            "expected": tick_reference(edge_ticks),
        },
    )


if __name__ == "__main__":
    main()
