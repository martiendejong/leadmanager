import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom'
import { AuthProvider } from './contexts/AuthContext'
import { ToastProvider } from './components/Toast'
import ProtectedRoute from './components/ProtectedRoute'
import Layout from './components/Layout'
import LoginPage from './pages/LoginPage'
import LeadsPage from './pages/LeadsPage'
import FinderPage from './pages/FinderPage'
import AdminUsersPage from './pages/AdminUsersPage'
import ProfilePage from './pages/ProfilePage'
import ClientsPage from './pages/ClientsPage'
import ClientDetailPage from './pages/ClientDetailPage'

function App() {
  return (
    <BrowserRouter>
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

            {/* Catch-all */}
            <Route path="*" element={<Navigate to="/leads" replace />} />
          </Routes>
        </AuthProvider>
      </ToastProvider>
    </BrowserRouter>
  )
}

export default App
