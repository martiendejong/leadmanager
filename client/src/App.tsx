import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom'
import { useEffect } from 'react'
import { AuthProvider } from './contexts/AuthContext'
import { ToastProvider } from './components/Toast'
import ProtectedRoute from './components/ProtectedRoute'
import Layout from './components/Layout'
import LoginPage from './pages/LoginPage'
import LeadsPage from './pages/LeadsPage'
import FinderPage from './pages/FinderPage'
import AdminUsersPage from './pages/AdminUsersPage'
import ProfilePage from './pages/ProfilePage'
import AnalyticsPage from './pages/AnalyticsPage'
import PipelinePage from './pages/PipelinePage'
import ClientsPage from './pages/ClientsPage'
import ClientDetailPage from './pages/ClientDetailPage'
import IntakePage from './pages/IntakePage'
import SalesSettingsPage from './pages/SalesSettingsPage'
import LeadDetailPage from './pages/LeadDetailPage'

// Handle SSO token injected by the backend into the URL fragment after redirect
function SsoTokenHandler() {
  useEffect(() => {
    const hash = window.location.hash
    const ssoToken = new URLSearchParams(hash.slice(1)).get('sso_token')
    const ssoError = new URLSearchParams(hash.slice(1)).get('sso_error')
    if (ssoToken) {
      localStorage.setItem('lm_token', ssoToken)
      window.location.replace('/leads')
    } else if (ssoError) {
      window.location.replace(`/login?sso_error=${encodeURIComponent(ssoError)}`)
    }
  }, [])
  return null
}

function App() {
  return (
    <BrowserRouter>
      <SsoTokenHandler />
      <ToastProvider>
        <AuthProvider>
          <Routes>
            {/* Public */}
            <Route path="/login" element={<LoginPage />} />

            {/* Root redirect */}
            <Route path="/" element={<Navigate to="/leads" replace />} />

            {/* Protected: regular users */}
            <Route
              path="/leads"
              element={
                <ProtectedRoute>
                  <Layout />
                </ProtectedRoute>
              }
            >
              <Route index element={<LeadsPage />} />
            </Route>

            <Route
              path="/leads/pipeline"
              element={
                <ProtectedRoute>
                  <Layout />
                </ProtectedRoute>
              }
            >
              <Route index element={<PipelinePage />} />
            </Route>

            <Route
              path="/leads/zoeken"
              element={
                <ProtectedRoute>
                  <Layout />
                </ProtectedRoute>
              }
            >
              <Route index element={<FinderPage />} />
            </Route>

            <Route
              path="/profile"
              element={
                <ProtectedRoute>
                  <Layout />
                </ProtectedRoute>
              }
            >
              <Route index element={<ProfilePage />} />
            </Route>

            <Route
              path="/leads/analytics"
              element={
                <ProtectedRoute>
                  <Layout />
                </ProtectedRoute>
              }
            >
              <Route index element={<AnalyticsPage />} />
            </Route>

            {/* Protected: Admin only */}
            <Route
              path="/admin/users"
              element={
                <ProtectedRoute requiredRole="Admin">
                  <Layout />
                </ProtectedRoute>
              }
            >
              <Route index element={<AdminUsersPage />} />
            </Route>

            {/* Clients */}
            <Route
              path="/clients"
              element={
                <ProtectedRoute>
                  <Layout />
                </ProtectedRoute>
              }
            >
              <Route index element={<ClientsPage />} />
            </Route>

            <Route
              path="/clients/:id"
              element={
                <ProtectedRoute>
                  <Layout />
                </ProtectedRoute>
              }
            >
              <Route index element={<ClientDetailPage />} />
            </Route>

            <Route
              path="/clients/:clientId/intake"
              element={
                <ProtectedRoute>
                  <Layout />
                </ProtectedRoute>
              }
            >
              <Route index element={<IntakePage />} />
            </Route>

            {/* Settings */}
            <Route
              path="/settings"
              element={
                <ProtectedRoute>
                  <Layout />
                </ProtectedRoute>
              }
            >
              <Route index element={<SalesSettingsPage />} />
            </Route>

            {/* Lead detail */}
            <Route
              path="/leads/:id"
              element={
                <ProtectedRoute>
                  <Layout />
                </ProtectedRoute>
              }
            >
              <Route index element={<LeadDetailPage />} />
            </Route>

            {/* Catch-all */}
            <Route path="*" element={<Navigate to="/leads" replace />} />
          </Routes>
        </AuthProvider>
      </ToastProvider>
    </BrowserRouter>
  )
}

export default App
