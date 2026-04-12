import type { Metadata } from 'next'
import { AuthProvider } from '@/lib/auth'
import './globals.css'

export const metadata: Metadata = {
  title: 'Themearr',
  description: 'Automatic movie theme downloader for Plex',
  icons: { icon: '/logo-icon.svg' },
}

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en" className="dark" style={{ colorScheme: 'dark' }}>
      <body>
        <AuthProvider>{children}</AuthProvider>
      </body>
    </html>
  )
}
