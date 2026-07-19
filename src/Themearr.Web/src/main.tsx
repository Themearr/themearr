import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter, Routes, Route } from 'react-router-dom'
import { AuthProvider } from '@/lib/auth'
import './app/globals.css'

import RootPage from './app/page'
import DashboardPage from './app/dashboard/page'
import HistoryPage from './app/history/page'
import LoginPage from './app/login/page'
import MoviesPage from './app/movies/page'
import QueuePage from './app/queue/page'
import SettingsPage from './app/settings/page'
import SetupPage from './app/setup/page'
import SystemPage from './app/system/page'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BrowserRouter>
      <AuthProvider>
        <Routes>
          <Route path="/" element={<RootPage />} />
          <Route path="/dashboard" element={<DashboardPage />} />
          <Route path="/history" element={<HistoryPage />} />
          <Route path="/login" element={<LoginPage />} />
          <Route path="/movies" element={<MoviesPage />} />
          <Route path="/queue" element={<QueuePage />} />
          <Route path="/settings" element={<SettingsPage />} />
          <Route path="/setup" element={<SetupPage />} />
          <Route path="/system" element={<SystemPage />} />
          {/* Unknown paths fall back to the root redirect, which routes by auth state. */}
          <Route path="*" element={<RootPage />} />
        </Routes>
      </AuthProvider>
    </BrowserRouter>
  </StrictMode>,
)
