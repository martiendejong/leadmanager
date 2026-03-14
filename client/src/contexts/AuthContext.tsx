import React, { createContext, useContext, useState, useEffect, useCallback } from 'react'
import { useNavigate } from 'react-router-dom'
import apiClient from '../api/client'

export interface User {
  id: string
  email: string
  firstName: string
  lastName: string
  role: string
}

interface AuthContextType {
  user: User | null
  token: string | null
  login: (email: string, password: string) => Promise<void>
  logout: () => void
  isLoading: boolean
}

const AuthContext = createContext<AuthContextType | null>(null)

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [user, setUser] = useState<User | null>(null)
  const [token, setToken] = useState<string | null>(localStorage.getItem('lm_token'))
  const [isLoading, setIsLoading] = useState<boolean>(true)
  const navigate = useNavigate()

  const logout = useCallback(() => {
    localStorage.removeItem('lm_token')
    setToken(null)
    setUser(null)
    navigate('/login')
  }, [navigate])

  // On mount: restore session if token exists
  useEffect(() => {
    const storedToken = localStorage.getItem('lm_token')
    if (!storedToken) {
      setIsLoading(false)
      return
    }
    apiClient
      .get<User>('/api/auth/me')
      .then((res) => {
        setUser(res.data)
        setToken(storedToken)
      })
      .catch(() => {
        localStorage.removeItem('lm_token')
        setToken(null)
      })
      .finally(() => setIsLoading(false))
  }, [])

  const login = async (email: string, password: string) => {
    const res = await apiClient.post<{ token: string; user: User }>('/api/auth/login', {
      email,
      password,
    })
    const { token: newToken, user: newUser } = res.data
    localStorage.setItem('lm_token', newToken)
    setToken(newToken)
    setUser(newUser)
  }

  return (
    <AuthContext.Provider value={{ user, token, login, logout, isLoading }}>
      {children}
    </AuthContext.Provider>
  )
}

export function useAuth(): AuthContextType {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used within AuthProvider')
  return ctx
}
