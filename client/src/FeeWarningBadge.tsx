const MISMATCH_PREFIX = "V5_FEE_RATE_MISMATCH";
const EXPIRING_PREFIX = "V5_FEE_RATE_EXPIRING";

export default function FeeWarningBadge({ warnings }: { warnings?: string[] | null }) {
  const list = warnings ?? [];
  if (list.length === 0) return null;
  const hasMismatch = list.some((w) => w.startsWith(MISMATCH_PREFIX));
  const hasExpiring = list.some((w) => w.startsWith(EXPIRING_PREFIX));
  const label = hasMismatch
    ? "⚠ 수수료 불일치"
    : hasExpiring
      ? "⚠ 수수료 만료 임박"
      : null;
  if (!label) return null;
  return (
    <div className="fee-warning-badge" title={list.join(" · ")}>
      {label}
    </div>
  );
}
