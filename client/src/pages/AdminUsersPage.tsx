import { useState, useEffect } from 'react'
import type { FormEvent } from 'react'
import apiClient from '../api/client'
import { useToast } from '../components/Toast'

interface AdminUser {
  id: string
  email: string
  firstName: string
  lastName: string
  role: string
  isActive: boolean
  createdAt: string
}

interface UserFormData {
  email: string
  firstName: string
  lastName: string
  password: string
  role: string
}

interface EditFormData {
  firstName: string
  lastName: string
  role: string
  isActive: boolean
}

const ROLES = ['Admin', 'User']

function Modal({
  title,
  onClose,
  children,
}: {
  title: string
  onClose: () => void
  children: React.ReactNode
}) {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 px-4">
      <div className="bg-white rounded-2xl shadow-xl w-full max-w-md p-6">
        <div className="flex items-center justify-between mb-5">
          <h3 className="text-lg font-semibold text-gray-900">{title}</h3>
          <button
            onClick={onClose}
            className="text-gray-400 hover:text-gray-600 text-xl font-bold leading-none"
            aria-label="Sluit"
          >
            ×
          </button>
        </div>
        {children}
      </div>
    </div>
  )
}

function AddUserModal({
  onClose,
  onSaved,
}: {
  onClose: () => void
  onSaved: () => void
}) {
  const { showToast } = useToast()
  const [form, setForm] = useState<UserFormData>({
    email: '',
    firstName: '',
    lastName: '',
    password: '',
    role: 'User',
  })
  const [saving, setSaving] = useState(false)
  const [errors, setErrors] = useState<Partial<UserFormData>>({})

  const validate = (): boolean => {
    const e: Partial<UserFormData> = {}
    if (!form.email.trim()) e.email = 'Vul een e-mailadres in'
    else if (!form.email.includes('@')) e.email = 'Vul een geldig e-mailadres in'
    if (!form.firstName.trim()) e.firstName = 'Vul een voornaam in'
    if (!form.lastName.trim()) e.lastName = 'Vul een achternaam in'
    if (!form.password) e.password = 'Vul een wachtwoord in'
    else if (form.password.length < 6) e.password = 'Wachtwoord moet minimaal 6 tekens zijn'
    setErrors(e)
    return Object.keys(e).length === 0
  }

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault()
    if (!validate()) return
    setSaving(true)
    try {
      await apiClient.post('/api/admin/users', form)
      showToast('Gebruiker aangemaakt', 'success')
      onSaved()
      onClose()
    } catch {
      showToast('Opslaan mislukt', 'error')
    } finally {
      setSaving(false)
    }
  }

  return (
    <Modal title="Gebruiker toevoegen" onClose={onClose}>
      <form onSubmit={handleSubmit} className="space-y-4">
        <Field label="E-mailadres">
          <input
            type="email"
            value={form.email}
            onChange={(e) => setForm({ ...form, email: e.target.value })}
            className={inputCls}
          />
          {errors.email && <p className="text-red-500 text-xs mt-1">{errors.email}</p>}
        </Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Voornaam">
            <input
              value={form.firstName}
              onChange={(e) => setForm({ ...form, firstName: e.target.value })}
              className={inputCls}
            />
            {errors.firstName && <p className="text-red-500 text-xs mt-1">{errors.firstName}</p>}
          </Field>
          <Field label="Achternaam">
            <input
              value={form.lastName}
              onChange={(e) => setForm({ ...form, lastName: e.target.value })}
              className={inputCls}
            />
            {errors.lastName && <p className="text-red-500 text-xs mt-1">{errors.lastName}</p>}
          </Field>
        </div>
        <Field label="Wachtwoord">
          <input
            type="password"
            value={form.password}
            onChange={(e) => setForm({ ...form, password: e.target.value })}
            className={inputCls}
          />
          {errors.password && <p className="text-red-500 text-xs mt-1">{errors.password}</p>}
        </Field>
        <Field label="Rol">
          <select
            value={form.role}
            onChange={(e) => setForm({ ...form, role: e.target.value })}
            className={inputCls}
          >
            {ROLES.map((r) => (
              <option key={r} value={r}>
                {r}
              </option>
            ))}
          </select>
        </Field>
        <div className="flex justify-end gap-3 pt-2">
          <button type="button" onClick={onClose} className={secondaryBtn}>
            Annuleer
          </button>
          <button type="submit" disabled={saving} className={primaryBtn}>
            {saving ? 'Opslaan...' : 'Opslaan'}
          </button>
        </div>
      </form>
    </Modal>
  )
}

function EditUserModal({
  user,
  onClose,
  onSaved,
}: {
  user: AdminUser
  onClose: () => void
  onSaved: () => void
}) {
  const { showToast } = useToast()
  const [form, setForm] = useState<EditFormData>({
    firstName: user.firstName,
    lastName: user.lastName,
    role: user.role,
    isActive: user.isActive,
  })
  const [saving, setSaving] = useState(false)

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault()
    setSaving(true)
    try {
      await apiClient.put(`/api/admin/users/${user.id}`, form)
      showToast('Gebruiker bijgewerkt', 'success')
      onSaved()
      onClose()
    } catch {
      showToast('Opslaan mislukt', 'error')
    } finally {
      setSaving(false)
    }
  }

  return (
    <Modal title={`Bewerk ${user.firstName} ${user.lastName}`} onClose={onClose}>
      <form onSubmit={handleSubmit} className="space-y-4">
        <div className="grid grid-cols-2 gap-3">
          <Field label="Voornaam">
            <input
              required
              value={form.firstName}
              onChange={(e) => setForm({ ...form, firstName: e.target.value })}
              className={inputCls}
            />
          </Field>
          <Field label="Achternaam">
            <input
              required
              value={form.lastName}
              onChange={(e) => setForm({ ...form, lastName: e.target.value })}
              className={inputCls}
            />
          </Field>
        </div>
        <Field label="Rol">
          <select
            value={form.role}
            onChange={(e) => setForm({ ...form, role: e.target.value })}
            className={inputCls}
          >
            {ROLES.map((r) => (
              <option key={r} value={r}>
                {r}
              </option>
            ))}
          </select>
        </Field>
        <div className="flex items-center gap-3">
          <input
            id="isActive"
            type="checkbox"
            checked={form.isActive}
            onChange={(e) => setForm({ ...form, isActive: e.target.checked })}
            className="h-4 w-4 rounded border-gray-300 text-indigo-600 focus:ring-indigo-500"
          />
          <label htmlFor="isActive" className="text-sm text-gray-700">
            Account actief
          </label>
        </div>
        <div className="flex justify-end gap-3 pt-2">
          <button type="button" onClick={onClose} className={secondaryBtn}>
            Annuleer
          </button>
          <button type="submit" disabled={saving} className={primaryBtn}>
            {saving ? 'Opslaan...' : 'Opslaan'}
          </button>
        </div>
      </form>
    </Modal>
  )
}

// Shared field wrapper
function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <label className="block text-sm font-medium text-gray-700 mb-1">{label}</label>
      {children}
    </div>
  )
}

// Shared style constants
const inputCls =
  'w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent'
const primaryBtn =
  'bg-indigo-600 hover:bg-indigo-700 disabled:opacity-60 text-white text-sm font-medium px-4 py-2 rounded-lg transition-colors'
const secondaryBtn =
  'bg-white hover:bg-gray-50 border border-gray-300 text-gray-700 text-sm font-medium px-4 py-2 rounded-lg transition-colors'

export default function AdminUsersPage() {
  const { showToast } = useToast()
  const [users, setUsers] = useState<AdminUser[]>([])
  const [loading, setLoading] = useState(true)
  const [showAdd, setShowAdd] = useState(false)
  const [editUser, setEditUser] = useState<AdminUser | null>(null)

  const fetchUsers = async () => {
    try {
      const res = await apiClient.get<AdminUser[]>('/api/admin/users')
      setUsers(res.data)
    } catch {
      showToast('Gebruikers laden mislukt', 'error')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    fetchUsers()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const toggleActive = async (user: AdminUser) => {
    const action = user.isActive ? 'deactiveren' : 'activeren'
    const warning = user.isActive ? ' De gebruiker kan daarna niet meer inloggen.' : ''
    const confirmed = window.confirm(
      `Weet je zeker dat je ${user.firstName} ${user.lastName} wilt ${action}?${warning}`
    )
    if (!confirmed) return
    try {
      await apiClient.put(`/api/admin/users/${user.id}`, {
        firstName: user.firstName,
        lastName: user.lastName,
        role: user.role,
        isActive: !user.isActive,
      })
      showToast(user.isActive ? 'Gebruiker gedeactiveerd' : 'Gebruiker geactiveerd', 'success')
      fetchUsers()
    } catch {
      showToast('Actie mislukt', 'error')
    }
  }

  const formatDate = (dateStr: string) =>
    new Date(dateStr).toLocaleDateString('nl-NL', {
      day: '2-digit',
      month: 'short',
      year: 'numeric',
    })

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <h2 className="text-2xl font-bold text-gray-900">Gebruikers</h2>
        <button onClick={() => setShowAdd(true)} className={primaryBtn}>
          + Gebruiker toevoegen
        </button>
      </div>

      {loading ? (
        <div className="flex items-center justify-center py-16">
          <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600" />
        </div>
      ) : (
        <div className="bg-white rounded-2xl border border-gray-200 shadow-sm overflow-hidden">
          <table className="w-full text-sm">
            <thead className="bg-gray-50 border-b border-gray-200">
              <tr>
                <th className="text-left text-xs font-semibold text-gray-500 uppercase tracking-wider px-5 py-3">
                  Email
                </th>
                <th className="text-left text-xs font-semibold text-gray-500 uppercase tracking-wider px-5 py-3">
                  Naam
                </th>
                <th className="text-left text-xs font-semibold text-gray-500 uppercase tracking-wider px-5 py-3">
                  Rol
                </th>
                <th className="text-left text-xs font-semibold text-gray-500 uppercase tracking-wider px-5 py-3">
                  Status
                </th>
                <th className="text-left text-xs font-semibold text-gray-500 uppercase tracking-wider px-5 py-3">
                  Aangemaakt
                </th>
                <th className="px-5 py-3" />
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {users.length === 0 ? (
                <tr>
                  <td colSpan={6} className="text-center text-gray-400 py-10">
                    Geen gebruikers gevonden
                  </td>
                </tr>
              ) : (
                users.map((u) => (
                  <tr key={u.id} className="hover:bg-gray-50 transition-colors">
                    <td className="px-5 py-3 text-gray-900">{u.email}</td>
                    <td className="px-5 py-3 text-gray-700">
                      {u.firstName} {u.lastName}
                    </td>
                    <td className="px-5 py-3">
                      <span
                        className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${
                          u.role === 'Admin'
                            ? 'bg-indigo-100 text-indigo-700'
                            : 'bg-gray-100 text-gray-700'
                        }`}
                      >
                        {u.role}
                      </span>
                    </td>
                    <td className="px-5 py-3">
                      <span
                        className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${
                          u.isActive
                            ? 'bg-green-100 text-green-700'
                            : 'bg-red-100 text-red-700'
                        }`}
                      >
                        {u.isActive ? 'Actief' : 'Inactief'}
                      </span>
                    </td>
                    <td className="px-5 py-3 text-gray-500">{formatDate(u.createdAt)}</td>
                    <td className="px-5 py-3">
                      <div className="flex items-center gap-2 justify-end">
                        <button
                          onClick={() => setEditUser(u)}
                          className="text-indigo-600 hover:text-indigo-800 font-medium text-xs px-2 py-1 rounded hover:bg-indigo-50 transition-colors"
                        >
                          Bewerk
                        </button>
                        <button
                          onClick={() => toggleActive(u)}
                          className={`text-xs font-medium px-2 py-1 rounded transition-colors ${
                            u.isActive
                              ? 'text-red-600 hover:text-red-800 hover:bg-red-50'
                              : 'text-green-600 hover:text-green-800 hover:bg-green-50'
                          }`}
                        >
                          {u.isActive ? 'Deactiveer' : 'Activeer'}
                        </button>
                      </div>
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      )}

      {showAdd && <AddUserModal onClose={() => setShowAdd(false)} onSaved={fetchUsers} />}
      {editUser && (
        <EditUserModal
          user={editUser}
          onClose={() => setEditUser(null)}
          onSaved={fetchUsers}
        />
      )}
    </div>
  )
}
