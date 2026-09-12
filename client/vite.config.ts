import { execSync } from "node:child_process";
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// 이슈 #161: 릴리즈 절차가 태그 체크아웃 후 빌드하므로 빌드 시점 태그가 배포 버전과 일치한다.
function releaseVersion() {
  try {
    return execSync("git describe --tags --abbrev=0", { stdio: ["ignore", "pipe", "ignore"] })
      .toString()
      .trim();
  } catch {
    return "dev";
  }
}

export default defineConfig({
  plugins: [react()],
  server: { port: 5173, proxy: { "/api": "http://127.0.0.1:5188" } },
  define: { __ASTRA_VERSION__: JSON.stringify(releaseVersion()) },
});
