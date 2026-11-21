import { useState } from 'react'
import { moveLeadToOrganization } from '../../api/leads'
import type { Lead } from '../../api/leads'
import { errorMessage } from '../../api/hunt'
import { useOrganizations } from '../../contexts/OrganizationContext'
import { useToast } from '../Toast'
import OrganizationBadge from './OrganizationBadge'

/**
 * The organisation of a lead: the badge, and (for a person who is in more than one organisation) a way to move the lead to another
 * organisation they are a member of. The people of the old organisation no longer see the lead afterwards.
 */
export default function LeadOrganization({ lead, onMoved }: { lead: Pick<Lead, 'id' | 'name' | 'organizationId'>; onMoved: (updated: Lead) => void }) {
  const { organizations, orgName, rememberOrg } = useOrganizations()
  const { showToast } = useToast()
  const [choosing, setChoosing] = useState(false)
  const [busy, setBusy] = useState(false)

  const others = organizations.filter((o) => o.id !== lead.organizationId)

  const move = async (targetId: string) => {
    if (!targetId) return
    const target = orgName(targetId)
    if (!window.confirm(`Move "${lead.name}" to ${target}? The members of ${orgName(lead.organizationId) || 'its current organisation'} will no longer see it.`)) {
      setChoosing(false)
      return
    }
    setBusy(true)
    try {
      const updated = await moveLeadToOrganization(lead.id, targetId)
      rememberOrg(targetId)
      showToast(`Moved to ${target}.`, 'success')
      setChoosing(false)
      onMoved(updated)
    } catch (err) {
      showToast(errorMessage(err, 'Could not move the lead'), 'error')
    } finally {
      setBusy(false)
    }
  }

  return (
    <span className="inline-flex flex-wrap items-center gap-2 max-w-full">
      <OrganizationBadge organizationId={lead.organizationId} />
      {others.length > 0 && !choosing && (
        <button type="button" onClick={() => setChoosing(true)} className="text-xs text-indigo-600 hover:underline">
          Move
        </button>
      )}
      {choosing && (
        <select
          autoFocus
          aria-label="Move this lead to organisation"
          disabled={busy}
          defaultValue=""
          onChange={(e) => move(e.target.value)}
          onBlur={() => setChoosing(false)}
          className="text-xs border border-gray-300 rounded-md px-2 py-1 bg-white max-w-full"
        >
          <option value="" disabled>Move to…</option>
          {others.map((o) => <option key={o.id} value={o.id}>{o.name}</option>)}
        </select>
      )}
    </span>
  )
}
