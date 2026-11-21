import React, { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react'
import { useAuth } from './AuthContext'
import { fetchOrganizations } from '../api/organizations'
import type { Organization } from '../api/organizations'

interface OrganizationContextType {
  /** The organisations the person is a member of */
  organizations: Organization[]
  /** False until the first load has finished */
  loaded: boolean
  /** Selected organisation ids in the filter; empty = all my organisations */
  selectedIds: string[]
  setSelectedIds: (ids: string[]) => void
  /** Value for the organizationId query parameter (comma separated), undefined = all */
  filterParam: string | undefined
  /** The organisation a new lead goes to: the one in the filter, else the last used, else the only one, else undefined (choose) */
  defaultOrgId: string | undefined
  /** Remember the organisation that was just used for creating something */
  rememberOrg: (id: string) => void
  orgName: (id: string | undefined | null) => string
  refresh: () => Promise<void>
}

const OrganizationContext = createContext<OrganizationContextType | null>(null)

const filterKey = (userId: string) => `lm_org_filter:${userId}`
const lastKey = (userId: string) => `lm_org_last:${userId}`

function readJson<T>(key: string, fallback: T): T {
  try {
    const raw = localStorage.getItem(key)
    return raw ? (JSON.parse(raw) as T) : fallback
  } catch {
    return fallback
  }
}

function writeJson(key: string, value: unknown) {
  try {
    localStorage.setItem(key, JSON.stringify(value))
  } catch {
    // private mode or full storage: the choice just is not remembered
  }
}

export function OrganizationProvider({ children }: { children: React.ReactNode }) {
  const { user } = useAuth()
  const userId = user?.id
  const [organizations, setOrganizations] = useState<Organization[]>([])
  const [loaded, setLoaded] = useState(false)
  const [stored, setStored] = useState<string[]>([])
  const [lastUsed, setLastUsed] = useState<string | undefined>(undefined)

  const refresh = useCallback(async () => {
    if (!userId) return
    try {
      setOrganizations(await fetchOrganizations())
    } catch {
      setOrganizations([])
    } finally {
      setLoaded(true)
    }
  }, [userId])

  // Per user: reload the organisations and the remembered choices when the person changes
  useEffect(() => {
    if (!userId) return
    setStored(readJson<string[]>(filterKey(userId), []))
    setLastUsed(readJson<string | undefined>(lastKey(userId), undefined))
    refresh()
  }, [userId, refresh])

  // A remembered organisation the person has left (or that no longer exists) is ignored
  const selectedIds = useMemo(
    () => (userId && loaded ? stored.filter((id) => organizations.some((o) => o.id === id)) : []),
    [userId, stored, organizations, loaded],
  )

  const setSelectedIds = useCallback(
    (ids: string[]) => {
      setStored(ids)
      if (userId) writeJson(filterKey(userId), ids)
    },
    [userId],
  )

  const rememberOrg = useCallback(
    (id: string) => {
      setLastUsed(id)
      if (userId) writeJson(lastKey(userId), id)
    },
    [userId],
  )

  const defaultOrgId = useMemo(() => {
    if (selectedIds.length === 1) return selectedIds[0]
    if (lastUsed && organizations.some((o) => o.id === lastUsed)) return lastUsed
    if (organizations.length === 1) return organizations[0].id
    return undefined
  }, [selectedIds, lastUsed, organizations])

  const orgName = useCallback(
    (id: string | undefined | null) => organizations.find((o) => o.id === id)?.name ?? '',
    [organizations],
  )

  const value = useMemo<OrganizationContextType>(
    () => ({
      organizations: userId ? organizations : [],
      loaded: userId ? loaded : false,
      selectedIds,
      setSelectedIds,
      filterParam: selectedIds.length > 0 ? selectedIds.join(',') : undefined,
      defaultOrgId,
      rememberOrg,
      orgName,
      refresh,
    }),
    [userId, organizations, loaded, selectedIds, setSelectedIds, defaultOrgId, rememberOrg, orgName, refresh],
  )

  return <OrganizationContext.Provider value={value}>{children}</OrganizationContext.Provider>
}

// eslint-disable-next-line react-refresh/only-export-components
export function useOrganizations(): OrganizationContextType {
  const ctx = useContext(OrganizationContext)
  if (!ctx) throw new Error('useOrganizations must be used within OrganizationProvider')
  return ctx
}
