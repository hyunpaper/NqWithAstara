// 이슈 #6 커버리지 캠페인 — vitest 단발 실행(jsdom) + v8 커버리지 설정.
// `npm test` = `vitest run --coverage` (watch 아님). CI test 잡과 로컬 검증이 같은 명령을 쓴다.
// JSX 변환은 vite 내장 esbuild(tsconfig `jsx: react-jsx`)로 충분해 별도 플러그인을 얹지 않는다.
import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    environment: "jsdom",
    include: ["src/**/*.test.{ts,tsx}"],
    coverage: {
      provider: "v8",
      // json-summary는 .github/scripts/coverage-summary.mjs가 Step Summary 표로 파싱한다.
      reporter: ["text", "json-summary", "lcov"],
      reportsDirectory: "coverage",
      // 기준선을 정직하게 잡기 위해 진입점(main.tsx) 포함 src 전체를 분모로 둔다.
      include: ["src/**/*.{ts,tsx}"],
      exclude: ["src/**/*.test.{ts,tsx}", "src/vite-env.d.ts"],
    },
  },
});
