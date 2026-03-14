import { NavLink, Outlet } from 'react-router-dom'
import { useAuth } from '../contexts/AuthContext'

export default function Layout() {
  const { user, logout } = useAuth()

  const linkClass = ({ isActive }: { isActive: boolean }) =>
    isActive
      ? 'text-indigo-600 font-medium border-b-2 border-indigo-600 pb-1'
      : 'text-gray-600 hover:text-indigo-600 transition-colors'

  return (
    <div className="min-h-screen bg-gray-50 flex flex-col">
      {/* Navbar */}
      <header className="bg-white border-b border-gray-200 sticky top-0 z-40 shadow-sm">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 h-16 flex items-center justify-between gap-6">
          {/* Logo */}
          <div className="flex-shrink-0">
            <span className="text-xl font-bold text-indigo-700 tracking-tight">
              LeadManager
            </span>
          </div>

          {/* Center nav */}
          <nav className="flex items-center gap-6 text-sm">
            <NavLink to="/leads" className={linkClass} end>
              Leads
            </NavLink>
            <NavLink to="/leads/zoeken" className={linkClass}>
              Leads zoeken
            </NavLink>
            <NavLink to="/profile" className={linkClass}>
              Profiel
            </NavLink>
            {user?.role === 'Admin' && (
              <NavLink to="/admin/users" className={linkClass}>
                Gebruikers
              </NavLink>
            )}
          </nav>

          {/* Right: user + logout */}
          <div className="flex items-center gap-3 flex-shrink-0">
            <span className="text-sm text-gray-700 font-medium">{user?.firstName}</span>
            <button
              onClick={logout}
              className="text-sm bg-gray-100 hover:bg-gray-200 text-gray-700 px-3 py-1.5 rounded-lg transition-colors"
            >
              Uitloggen
            </button>
          </div>
        </div>
      </header>

      {/* Page content */}
      <main className="flex-1 max-w-7xl w-full mx-auto px-4 sm:px-6 lg:px-8 py-8">
        <Outlet />
      </main>
    </div>
  )
}
