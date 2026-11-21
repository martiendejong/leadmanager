import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { useAuth } from '../contexts/AuthContext'
import { useOrganizations } from '../contexts/OrganizationContext'
import { useToast } from '../components/Toast'
import { errorMessage } from '../api/hunt'
import {
  addOrganizationMember,
  createOrganization,
  fetchOrganizationMembers,
  removeOrganizationMember,
  updateOrganizationMember,
} from '../api/organizations'
import type { Organization, OrganizationMember } from '../api/organizations'
import { orgColour } from '../components/organizations/OrganizationBadge'

const input = 'border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500 min-w-0'

function MembersPanel({ org, onChanged }: { org: Organization; onChanged: () => void }) {
  const { showToast } = useToast()
  const { user } = useAuth()
  const isAdmin = org.role === 'admin'
  const [members, setMembers] = useState<OrganizationMember[] | null>(null)
  const [email, setEmail] = useState('')
  const [role, setRole] = useState<'admin' | 'member'>('member')
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    try {
      setMembers(await fetchOrganizationMembers(org.id))
    } catch (err) {
      showToast(errorMessage(err, 'Could not load the members'), 'error')
      setMembers([])
    }
  }, [org.id, showToast])

  useEffect(() => {
    void load()
  }, [load])

  const add = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!email.trim()) return
    setBusy(true)
    try {
      const m = await addOrganizationMember(org.id, email.trim(), role)
      showToast(m.pending ? `${m.email} is added. They get access when they first log in.` : `${m.email} is added.`, 'success')
      setEmail('')
      setRole('member')
      await load()
      onChanged()
    } catch (err) {
      showToast(errorMessage(err, 'Could not add the member'), 'error')
    } finally {
      setBusy(false)
    }
  }

  const changeRole = async (m: OrganizationMember, next: 'admin' | 'member') => {
    try {
      await updateOrganizationMember(org.id, m.id, next)
      await load()
    } catch (err) {
      showToast(errorMessage(err, 'Could not change the role'), 'error')
    }
  }

  const remove = async (m: OrganizationMember) => {
    if (!window.confirm(`Remove ${m.email} from ${org.name}? They no longer see its leads.`)) return
    try {
      await removeOrganizationMember(org.id, m.id)
      await load()
      onChanged()
    } catch (err) {
      showToast(errorMessage(err, 'Could not remove the member'), 'error')
    }
  }

  return (
    <div className="mt-4 border-t border-gray-100 pt-4">
      <h3 className="text-xs font-semibold text-gray-500 uppercase tracking-wider mb-2">Members</h3>
      {members === null && <p className="text-sm text-gray-400">Loading…</p>}
      <ul className="divide-y divide-gray-100">
        {members?.map((m) => (
          <li key={m.id} className="py-2 flex flex-wrap items-center gap-x-3 gap-y-2">
            <div className="min-w-0 flex-1 basis-48">
              <p className="text-sm text-gray-900 break-all">{m.name ?? m.email}</p>
              {m.name && <p className="text-xs text-gray-500 break-all">{m.email}</p>}
              {m.pending && <p className="text-xs text-amber-700">Pending: no account yet, access starts at their first login</p>}
            </div>
            {isAdmin ? (
              <select
                aria-label={`Role of ${m.email}`}
                className={`${input} py-1`}
                value={m.role}
                onChange={(e) => changeRole(m, e.target.value as 'admin' | 'member')}
              >
                <option value="member">Member</option>
                <option value="admin">Admin</option>
              </select>
            ) : (
              <span className="text-xs px-2 py-0.5 rounded-full bg-gray-100 text-gray-600">{m.role === 'admin' ? 'Admin' : 'Member'}</span>
            )}
            {isAdmin && (
              <button
                type="button"
                onClick={() => remove(m)}
                className="text-xs px-2.5 py-1 rounded-lg border border-red-200 text-red-700 hover:bg-red-50"
                aria-label={`Remove ${m.email}`}
              >
                {m.email === user?.email?.toLowerCase() ? 'Leave' : 'Remove'}
              </button>
            )}
          </li>
        ))}
      </ul>

      {isAdmin ? (
        <form onSubmit={add} className="mt-4 flex flex-wrap gap-2 items-end">
          <div className="flex-1 basis-56 min-w-0">
            <label htmlFor={`member-email-${org.id}`} className="block text-xs font-medium text-gray-600 mb-1">Add by e-mail address</label>
            <input
              id={`member-email-${org.id}`}
              type="email"
              required
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              placeholder="name@example.com"
              className={`${input} w-full`}
            />
          </div>
          <div>
            <label htmlFor={`member-role-${org.id}`} className="block text-xs font-medium text-gray-600 mb-1">Role</label>
            <select id={`member-role-${org.id}`} value={role} onChange={(e) => setRole(e.target.value as 'admin' | 'member')} className={input}>
              <option value="member">Member</option>
              <option value="admin">Admin</option>
            </select>
          </div>
          <button type="submit" disabled={busy || !email.trim()} className="px-4 py-2 rounded-lg bg-indigo-600 text-white text-sm hover:bg-indigo-700 disabled:opacity-50">
            Add
          </button>
        </form>
      ) : (
        <p className="mt-3 text-xs text-gray-500">Only an organisation admin can add or remove members.</p>
      )}
    </div>
  )
}

/** Settings > Organisations: the organisations of the person and their members. */
export default function OrganizationsPage() {
  const { showToast } = useToast()
  const { user } = useAuth()
  const { organizations, loaded, refresh } = useOrganizations()
  const [openId, setOpenId] = useState<string | null>(null)
  const [newName, setNewName] = useState('')

  const create = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!newName.trim()) return
    try {
      const org = await createOrganization(newName.trim())
      setNewName('')
      await refresh()
      setOpenId(org.id)
    } catch (err) {
      showToast(errorMessage(err, 'Could not create the organisation'), 'error')
    }
  }

  return (
    <div className="max-w-3xl mx-auto">
      <div className="mb-4">
        <h1 className="text-2xl font-bold text-gray-900">Settings</h1>
        <p className="text-gray-500 text-sm mt-1">Organisations decide who sees which leads.</p>
      </div>
      <div className="flex gap-1 mb-6 flex-wrap border-b border-gray-200">
        <Link to="/settings" className="px-4 py-2 text-sm font-medium rounded-t-lg text-gray-600 hover:text-indigo-600 hover:bg-indigo-50">
          Sales settings
        </Link>
        <span className="px-4 py-2 text-sm font-medium rounded-t-lg bg-indigo-600 text-white" aria-current="page">Organisations</span>
      </div>

      {!loaded && <p className="text-sm text-gray-400">Loading…</p>}
      {loaded && organizations.length === 0 && (
        <div role="status" className="bg-amber-50 border border-amber-200 rounded-xl p-5 text-sm text-amber-900">
          You are not a member of any organisation yet, so you see no leads. Ask an organisation admin to add you with your e-mail address
          {user?.email ? <> (<span className="font-medium break-all">{user.email}</span>)</> : null}.
        </div>
      )}

      <ul className="space-y-3">
        {organizations.map((o) => {
          const open = openId === o.id
          return (
            <li key={o.id} className="bg-white rounded-xl border border-gray-200 p-4">
              <button
                type="button"
                onClick={() => setOpenId(open ? null : o.id)}
                aria-expanded={open}
                className="w-full text-left flex flex-wrap items-center gap-x-3 gap-y-1"
              >
                <span className={`px-2.5 py-0.5 rounded-full border text-sm font-medium max-w-full truncate ${orgColour(o.id)}`}>{o.name}</span>
                <span className="text-xs text-gray-500">{o.role === 'admin' ? 'You are admin' : 'You are member'}</span>
                <span className="text-xs text-gray-400 sm:ml-auto">
                  {o.memberCount} {o.memberCount === 1 ? 'member' : 'members'} · {o.leadCount} {o.leadCount === 1 ? 'lead' : 'leads'}
                </span>
              </button>
              {open && <MembersPanel org={o} onChanged={refresh} />}
            </li>
          )
        })}
      </ul>

      {user?.role === 'Admin' && (
        <form onSubmit={create} className="mt-6 flex flex-wrap gap-2 items-end">
          <div className="flex-1 basis-56 min-w-0">
            <label htmlFor="new-organization" className="block text-xs font-medium text-gray-600 mb-1">New organisation</label>
            <input id="new-organization" value={newName} onChange={(e) => setNewName(e.target.value)} maxLength={100} placeholder="Name" className={`${input} w-full`} />
          </div>
          <button type="submit" disabled={newName.trim().length < 2} className="px-4 py-2 rounded-lg border border-indigo-300 text-indigo-700 text-sm hover:bg-indigo-50 disabled:opacity-50">
            Create
          </button>
        </form>
      )}
    </div>
  )
}
