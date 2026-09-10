import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import {
  getNotificationCount,
  getNotifications,
  markAllRead,
  markRead,
  type Notification,
} from '../api/notifications'

export default function NotificationBell() {
  const [count, setCount] = useState(0)
  const [open, setOpen] = useState(false)
  const [notifications, setNotifications] = useState<Notification[]>([])
  const [loading, setLoading] = useState(false)
  const dropdownRef = useRef<HTMLDivElement>(null)
  const navigate = useNavigate()

  // Poll count every 60 seconds
  useEffect(() => {
    let cancelled = false

    const fetchCount = async () => {
      try {
        const n = await getNotificationCount()
        if (!cancelled) setCount(n)
      } catch {
        // silently ignore — not critical
      }
    }

    fetchCount()
    const interval = setInterval(fetchCount, 60_000)
    return () => {
      cancelled = true
      clearInterval(interval)
    }
  }, [])

  // Close dropdown on outside click
  useEffect(() => {
    const onOutsideClick = (e: MouseEvent) => {
      if (dropdownRef.current && !dropdownRef.current.contains(e.target as Node)) {
        setOpen(false)
      }
    }
    document.addEventListener('mousedown', onOutsideClick)
    return () => document.removeEventListener('mousedown', onOutsideClick)
  }, [])

  const handleOpen = async () => {
    if (open) {
      setOpen(false)
      return
    }
    setOpen(true)
    setLoading(true)
    try {
      const items = await getNotifications()
      setNotifications(items)
    } catch {
      // ignore
    } finally {
      setLoading(false)
    }
  }

  const handleMarkOne = async (id: string) => {
    await markRead(id)
    setNotifications((prev) => prev.filter((n) => n.id !== id))
    setCount((c) => Math.max(0, c - 1))
  }

  const handleMarkAll = async () => {
    await markAllRead()
    setNotifications([])
    setCount(0)
  }

  const handleItemClick = async (notification: Notification) => {
    await handleMarkOne(notification.id)
    if (notification.linkedLeadId) {
      setOpen(false)
      navigate('/leads')
    }
  }

  const formatTime = (iso: string) => {
    try {
      return new Date(iso).toLocaleString('nl-NL', {
        day: '2-digit',
        month: '2-digit',
        hour: '2-digit',
        minute: '2-digit',
      })
    } catch {
      return iso
    }
  }

  const typeIcon = (type: string) => {
    switch (type) {
      case 'StaleLeadWarning':
        return '⚠️'
      case 'ReminderDue':
        return '🔔'
      case 'EnrichmentComplete':
        return '✅'
      default:
        return '📌'
    }
  }

  return (
    <div className="relative" ref={dropdownRef}>
      {/* Bell button */}
      <button
        onClick={handleOpen}
        className="relative p-2 rounded-lg text-gray-600 hover:text-indigo-600 hover:bg-gray-100 transition-colors"
        title="Meldingen"
        aria-label="Meldingen"
      >
        <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path
            strokeLinecap="round"
            strokeLinejoin="round"
            strokeWidth={2}
            d="M15 17h5l-1.405-1.405A2.032 2.032 0 0118 14.158V11a6.002 6.002 0 00-4-5.659V5a2 2 0 10-4 0v.341C7.67 6.165 6 8.388 6 11v3.159c0 .538-.214 1.055-.595 1.436L4 17h5m6 0v1a3 3 0 11-6 0v-1m6 0H9"
          />
        </svg>
        {count > 0 && (
          <span className="absolute -top-0.5 -right-0.5 inline-flex items-center justify-center w-4 h-4 text-xs font-bold text-white bg-red-500 rounded-full">
            {count > 9 ? '9+' : count}
          </span>
        )}
      </button>

      {/* Dropdown */}
      {open && (
        <div className="absolute right-0 mt-2 w-80 bg-white rounded-xl shadow-lg border border-gray-200 z-50 overflow-hidden">
          <div className="flex items-center justify-between px-4 py-3 border-b border-gray-100">
            <h3 className="text-sm font-semibold text-gray-800">Meldingen</h3>
            {notifications.length > 0 && (
              <button
                onClick={handleMarkAll}
                className="text-xs text-indigo-600 hover:text-indigo-800 font-medium transition-colors"
              >
                Alles gelezen
              </button>
            )}
          </div>

          <div className="max-h-72 overflow-y-auto">
            {loading && (
              <div className="px-4 py-6 text-center text-sm text-gray-400">
                Laden...
              </div>
            )}
            {!loading && notifications.length === 0 && (
              <div className="px-4 py-6 text-center text-sm text-gray-400">
                Geen ongelezen meldingen
              </div>
            )}
            {!loading &&
              notifications.map((n) => (
                <button
                  key={n.id}
                  onClick={() => handleItemClick(n)}
                  className="w-full text-left px-4 py-3 hover:bg-gray-50 border-b border-gray-50 last:border-0 transition-colors"
                >
                  <div className="flex items-start gap-2">
                    <span className="text-base mt-0.5 flex-shrink-0">{typeIcon(n.type)}</span>
                    <div className="flex-1 min-w-0">
                      <p className="text-sm text-gray-800 leading-snug">{n.message}</p>
                      <p className="text-xs text-gray-400 mt-0.5">{formatTime(n.createdAt)}</p>
                    </div>
                  </div>
                </button>
              ))}
          </div>
        </div>
      )}
    </div>
  )
}
