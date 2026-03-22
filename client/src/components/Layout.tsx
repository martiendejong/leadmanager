import { useState } from 'react'
import { NavLink, Outlet, useLocation } from 'react-router-dom'
import { useAuth } from '../contexts/AuthContext'

export default function Layout() {
  const { user, logout } = useAuth()
  const location = useLocation()
  const [menuOpen, setMenuOpen] = useState(false)

  const linkClass = ({ isActive }: { isActive: boolean }) =>
    isActive
      ? 'text-indigo-600 font-medium border-b-2 border-indigo-600 pb-1'
      : 'text-gray-600 hover:text-indigo-600 transition-colors'

  const mobileLinkClass = ({ isActive }: { isActive: boolean }) =>
    isActive
      ? 'block px-4 py-3 text-sm font-medium text-indigo-600 bg-indigo-50 border-l-4 border-indigo-600'
      : 'block px-4 py-3 text-sm text-gray-700 hover:bg-gray-50 hover:text-indigo-600 border-l-4 border-transparent transition-colors'

  // Close menu on navigation
  const closeMenu = () => setMenuOpen(false)

  // Nav items in correct order: Leads | Klanten | Profiel | Leads zoeken | Gebruikers
  const navItems = (
    <>
      <NavLink to="/leads" className={linkClass} end onClick={closeMenu}>Leads</NavLink>
      <NavLink to="/clients" className={linkClass} onClick={closeMenu}>Klanten</NavLink>
      <NavLink to="/profile" className={linkClass} onClick={closeMenu}>Profiel</NavLink>
      <NavLink to="/leads/zoeken" className={linkClass} onClick={closeMenu}>Leads zoeken</NavLink>
      {user?.role === 'Admin' && (
        <NavLink to="/admin/users" className={linkClass} onClick={closeMenu}>Gebruikers</NavLink>
      )}
    </>
  )

  const mobileNavItems = (
    <>
      <NavLink to="/leads" className={mobileLinkClass} end onClick={closeMenu}>Leads</NavLink>
      <NavLink to="/clients" className={mobileLinkClass} onClick={closeMenu}>Klanten</NavLink>
      <NavLink to="/profile" className={mobileLinkClass} onClick={closeMenu}>Profiel</NavLink>
      <NavLink to="/leads/zoeken" className={mobileLinkClass} onClick={closeMenu}>Leads zoeken</NavLink>
      {user?.role === 'Admin' && (
        <NavLink to="/admin/users" className={mobileLinkClass} onClick={closeMenu}>Gebruikers</NavLink>
      )}
    </>
  )

  // Close menu when route changes
  if (!menuOpen && location) { /* intentional — location dependency keeps menu closed on nav */ }

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

          {/* Desktop center nav */}
          <nav className="hidden sm:flex items-center gap-6 text-sm">
            {navItems}
          </nav>

          {/* Right: user + logout + hamburger */}
          <div className="flex items-center gap-3 flex-shrink-0">
            <span className="hidden sm:block text-sm text-gray-700 font-medium">{user?.firstName}</span>
            <button
              onClick={logout}
              className="hidden sm:block text-sm bg-gray-100 hover:bg-gray-200 text-gray-700 px-3 py-1.5 rounded-lg transition-colors"
            >
              Uitloggen
            </button>

            {/* Hamburger (mobile only) */}
            <button
              onClick={() => setMenuOpen(!menuOpen)}
              className="sm:hidden p-2 rounded-lg text-gray-600 hover:text-indigo-600 hover:bg-gray-100 transition-colors"
              aria-label={menuOpen ? 'Menu sluiten' : 'Menu openen'}
            >
              {menuOpen ? (
                <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                </svg>
              ) : (
                <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 6h16M4 12h16M4 18h16" />
                </svg>
              )}
            </button>
          </div>
        </div>

        {/* Mobile dropdown menu */}
        {menuOpen && (
          <div className="sm:hidden bg-white border-t border-gray-200 shadow-lg">
            <nav className="py-1">
              {mobileNavItems}
            </nav>
            <div className="px-4 py-3 border-t border-gray-100 flex items-center justify-between">
              <span className="text-sm text-gray-700 font-medium">{user?.firstName}</span>
              <button
                onClick={() => { logout(); closeMenu() }}
                className="text-sm bg-gray-100 hover:bg-gray-200 text-gray-700 px-3 py-1.5 rounded-lg transition-colors"
              >
                Uitloggen
              </button>
            </div>
          </div>
        )}
      </header>

      {/* Page content */}
      <main className="flex-1 max-w-7xl w-full mx-auto px-4 sm:px-6 lg:px-8 py-8">
        <Outlet />
      </main>
    </div>
  )
}
