'use client'

import Image from 'next/image'
import { useEffect, useRef, useState } from 'react'
import { useRouter } from 'next/navigation'
import { setupApi } from '@/lib/api'
import { useAuth } from '@/lib/auth'
import { Button, Spinner } from '@/components/ui'

export default function LoginPage() {
  const router = useRouter()
  const { loading, connected, setupComplete, refresh } = useAuth()
  const [error, setError] = useState('')
  const [polling, setPolling] = useState(false)
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null)

  // Redirect if already authenticated
  useEffect(() => {
    if (!loading && connected) {
      router.replace(setupComplete ? '/movies' : '/setup')
    }
  }, [loading, connected, setupComplete, router])

  // Handle return from Plex OAuth
  useEffect(() => {
    if (typeof window === 'undefined') return
    const params = new URLSearchParams(window.location.search)
    if (params.get('plexCallback') !== '1') return
    const saved = localStorage.getItem('plex_pin')
    if (!saved) return
    const { pinId, code } = JSON.parse(saved)
    localStorage.removeItem('plex_pin')
    window.history.replaceState({}, '', '/login')
    beginPolling(pinId, code)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useEffect(() => () => { if (pollRef.current) clearInterval(pollRef.current) }, [])

  async function startLogin() {
    setError('')
    try {
      const forwardUrl = `${window.location.origin}/login?plexCallback=1`
      const data = await setupApi.startPlexLogin(forwardUrl)
      localStorage.setItem('plex_pin', JSON.stringify({ pinId: data.pinId, code: data.code }))
      window.location.href = data.authUrl
    } catch (e) {
      setError((e as Error).message)
    }
  }

  function beginPolling(pinId: number, code: string) {
    setPolling(true)
    pollRef.current = setInterval(async () => {
      try {
        const status = await setupApi.plexLoginStatus(pinId, code)
        if (status.claimed) {
          clearInterval(pollRef.current!)
          setPolling(false)
          await refresh()
          const s = await setupApi.status()
          router.replace(s.setupComplete ? '/movies' : '/setup')
        }
      } catch { /* keep polling */ }
    }, 2000)
  }

  if (loading) {
    return (
      <div className="flex min-h-screen items-center justify-center">
        <Spinner size={32} className="text-[#BB0000]" />
      </div>
    )
  }

  return (
    <div className="flex min-h-screen items-center justify-center px-4 bg-[#0C111D]">
      <div className="w-full max-w-sm space-y-8">
        {/* Logo */}
        <div className="flex flex-col items-center gap-4">
          <Image src="/logo-icon.svg" alt="Themearr" width={80} height={80} />
          <Image src="/logo.svg" alt="Themearr" width={207} height={54} style={{ height: 32, width: 'auto' }} />
          <p className="text-sm text-[#667085]">Sign in to continue</p>
        </div>

        {error && (
          <div className="rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-4 py-3">
            <p className="text-sm text-[#FDA29B]">{error}</p>
          </div>
        )}

        <div className="rounded-xl border border-[#1D2939] bg-[#101828] p-6 space-y-4">
          {polling ? (
            <div className="space-y-3">
              <div className="flex items-center gap-3 text-sm text-[#98A2B3]">
                <Spinner size={18} />
                Waiting for Plex authorisation…
              </div>
              <button
                onClick={() => { setPolling(false); clearInterval(pollRef.current!) }}
                className="text-xs text-[#667085] hover:text-[#D0D5DD] transition-colors"
              >
                Cancel
              </button>
            </div>
          ) : (
            <>
              <Button onClick={startLogin} className="w-full">
                Sign in with Plex
              </Button>
              <p className="text-center text-xs text-[#475467]">
                You&apos;ll be redirected to Plex to authorise Themearr, then brought back automatically.
              </p>
            </>
          )}
        </div>
      </div>
    </div>
  )
}
