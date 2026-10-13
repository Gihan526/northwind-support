import { Badge, type BadgeTone } from '../../../components/Badge';
import { formatDateTime } from '../../../lib/format';
import type { SlaStatus } from '../../../types/api';

const labels: Record<SlaStatus, { text: string; tone: BadgeTone }> = {
  AtRisk: { text: 'At risk', tone: 'warning' },
  Met: { text: 'Met', tone: 'success' },
  Breached: { text: 'Breached', tone: 'danger' },
  WithinSla: { text: 'Within SLA', tone: 'info' },
  NotApplicable: { text: 'No SLA', tone: 'neutral' },
};

interface SlaCellProps {
  dueAtUtc: string | null;
  slaStatus: SlaStatus;
}

/** The due date and where the ticket stands against it, as shown in the ticket list. */
export function SlaCell({ dueAtUtc, slaStatus }: SlaCellProps) {
  const label = labels[slaStatus];

  return (
    <div className="sla-cell">
      <Badge tone={label.tone}>{label.text}</Badge>
      <span className="sla-cell__due">{formatDateTime(dueAtUtc)}</span>
    </div>
  );
}
