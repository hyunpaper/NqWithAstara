const MISMATCH_PREFIX = "V5_FEE_RATE_MISMATCH";

export default function FeeWarningBadge({ warnings }: { warnings?: string[] | null }) {
  const list = warnings ?? [];
  if (list.length === 0) return null;
  const mismatchWarnings = list.filter((w) => w.startsWith(MISMATCH_PREFIX));
  const hasMismatch = mismatchWarnings.length > 0;
  const label = hasMismatch ? "⚠ 수수료 불일치" : null;
  if (!label) return null;
  return (
    <div className="fee-warning-badge" title={mismatchWarnings.join(" · ")}>
      {label}
    </div>
  );
}
