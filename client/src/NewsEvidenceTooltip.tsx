import type { NewsArticle } from "./newsTypes";
import { absoluteTimeKst, sentimentBadge } from "./newsFormat";

export default function NewsEvidenceTooltip({ article }: { article: NewsArticle }) {
  const badge = sentimentBadge(article.sentiment);
  const impacts = Object.entries(article.impactScores ?? {});
  return <span className="news-evidence-tooltip" role="tooltip">
    {badge && <strong className={badge.className}>{badge.label}</strong>}
    {article.reason && <span>{article.reason}</span>}
    {impacts.map(([symbol, score]) => <span key={symbol}>{symbol} 영향도 {score >= 0 ? "+" : ""}{score.toFixed(1)}</span>)}
    {article.createdAt && <span>{absoluteTimeKst(article.createdAt)}</span>}
  </span>;
}
