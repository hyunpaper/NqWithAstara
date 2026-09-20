import type { NewsArticle } from "./newsTypes";
import { absoluteTimeKst, sentimentBadge } from "./newsFormat";

export default function NewsEvidenceTooltip({ article }: { article: NewsArticle }) {
  const badge = sentimentBadge(article.sentiment);
  const impacts = Object.entries(article.impactScores ?? {});
  return <span className="news-evidence-tooltip" role="tooltip" aria-label="뉴스 감성 판정 근거">
    {badge && <strong className={badge.className}>{badge.label}</strong>}
    {article.reason && <span>판정 근거: {article.reason}</span>}
    {article.classificationText && <span>분석 내용: {article.classificationText}</span>}
    {article.evidenceSource && <span>근거 원천: {article.evidenceSource}</span>}
    {article.inputKind && <span>분석 입력: {article.inputKind === "body" ? "본문" : article.inputKind === "summary" ? "요약" : "헤드라인"}</span>}
    {impacts.map(([symbol, score]) => <span key={symbol}>{symbol} 영향도 {score >= 0 ? "+" : ""}{score.toFixed(1)}</span>)}
    {article.evidenceArticleId && <span>동일 기사 분석 근거로 연결됨</span>}
    {article.evidence?.slice(0, 3).map((item) => <span key={item.id}>근거 기사: {item.titleKo ?? item.title} ({item.sourceKo ?? item.source ?? "출처 미상"})</span>)}
    {article.createdAt && <span>{absoluteTimeKst(article.createdAt)}</span>}
  </span>;
}
