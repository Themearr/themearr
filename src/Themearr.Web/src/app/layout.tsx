import type { Metadata } from 'next'
import './globals.css'

export const metadata: Metadata = {
  title: 'Themearr',
  description: 'Automatic movie theme downloader for Plex',
}

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en" className="dark" style={{ colorScheme: 'dark' }}>
      <body>{children}</body>
    </html>
  )
}
