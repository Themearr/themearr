import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { authApi, setupApi, setAuthToken } from '@/lib/api'
import { useAuth } from '@/lib/auth'
import { Button, Spinner } from '@/components/ui'

export default function LoginPage() {
  const navigate = useNavigate()
  const { loading, authorized, connected, setupComplete, refresh } = useAuth()
  const [token, setToken] = useState('')
  const [verifying, setVerifying] = useState(false)
  const [error, setError] = useState('')
  const [polling, setPolling] = useState(false)
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null)

  // Redirect once authorized, provided either Plex is connected or setup
  // (which may have been completed with a non-Plex source, e.g. Radarr)
  // already finished.
  useEffect(() => {
    if (!loading && authorized && (connected || setupComplete)) {
      navigate(setupComplete ? '/queue' : '/setup', { replace: true })
    }
  }, [loading, authorized, connected, setupComplete, navigate])

  // Declared before its first use below — a hoisted call reads as a stale
  // reference to React's compiler lint (react-hooks/immutability).
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
          navigate(s.setupComplete ? '/queue' : '/setup', { replace: true })
        }
      } catch { /* keep polling */ }
    }, 2000)
  }

  // Handle return from Plex OAuth.
  // If plex_pin is in localStorage when the page loads, we just came back from Plex.
  useEffect(() => {
    if (typeof window === 'undefined') return
    const saved = localStorage.getItem('plex_pin')
    if (!saved) return
    try {
      const { pinId, code } = JSON.parse(saved)
      localStorage.removeItem('plex_pin')
      window.history.replaceState({}, '', '/login')
      beginPolling(pinId, code)
    } catch {
      localStorage.removeItem('plex_pin')
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useEffect(() => () => { if (pollRef.current) clearInterval(pollRef.current) }, [])

  async function startLogin() {
    setError('')
    try {
      const forwardUrl = `${window.location.origin}/login`
      const data = await setupApi.startPlexLogin(forwardUrl)
      localStorage.setItem('plex_pin', JSON.stringify({ pinId: data.pinId, code: data.code }))
      window.location.href = data.authUrl
    } catch (e) {
      setError((e as Error).message)
    }
  }

  async function verifyToken(e: React.FormEvent) {
    e.preventDefault()
    setError('')
    setVerifying(true)
    try {
      const { ok } = await authApi.verify(token.trim())
      if (!ok) throw new Error('Invalid token')
      setAuthToken(token.trim())
      await refresh()
    } catch (err) {
      setError((err as Error).message || 'Invalid token')
    } finally {
      setVerifying(false)
    }
  }

  if (loading) {
    return (
      <div className="flex min-h-screen items-center justify-center">
        <Spinner size={32} className="text-[#BB0000]" />
      </div>
    )
  }

  // Stage 1 — app access token
  if (!authorized) {
    return (
      <div className="flex min-h-screen items-center justify-center px-4 bg-[#0C111D]">
        <div className="w-full max-w-sm space-y-8">
          <div className="flex flex-col items-center gap-4">
            <img src="/logo-icon.svg" alt="Themearr" width={80} height={80} />
            <img src="/logo.svg" alt="Themearr" width={207} height={54} style={{ height: 32, width: 'auto' }} />
            <p className="text-sm text-[#667085]">Enter your access token</p>
          </div>

          {error && (
            <div className="rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-4 py-3">
              <p className="text-sm text-[#FDA29B]">{error}</p>
            </div>
          )}

          <form onSubmit={verifyToken} className="rounded-xl border border-[#1D2939] bg-[#101828] p-6 space-y-4">
            <input
              type="password"
              value={token}
              onChange={e => setToken(e.target.value)}
              placeholder="Access token"
              autoFocus
              className="w-full rounded-md bg-[#0C111D] border border-[#1D2939] px-3 py-2 text-sm text-[#F9FAFB] placeholder-[#475467] focus:outline-none focus:border-[#BB0000]"
            />
            <Button type="submit" className="w-full" disabled={!token.trim() || verifying} loading={verifying}>
              Continue
            </Button>
            <p className="text-center text-xs text-[#475467]">
              The token is printed once on the server when you install Themearr. Look for <code className="text-[#667085]">/opt/themearr/data/auth.env</code> if you need to recover it.
            </p>
          </form>
        </div>
      </div>
    )
  }

  // Stage 2 — Plex OAuth (only shown after the bearer token is accepted)
  return (
    <div className="flex min-h-screen items-center justify-center px-4 bg-[#0C111D]">
      <div className="w-full max-w-sm space-y-8">
        {/* Logo */}
        <div className="flex flex-col items-center gap-4">
          <img src="/logo-icon.svg" alt="Themearr" width={80} height={80} />
          <img src="/logo.svg" alt="Themearr" width={207} height={54} style={{ height: 32, width: 'auto' }} />
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
              <div className="flex items-center gap-3">
                <div className="h-px flex-1 bg-[#1D2939]" />
                <span className="text-xs text-[#475467]">or</span>
                <div className="h-px flex-1 bg-[#1D2939]" />
              </div>
              <button
                onClick={() => navigate('/setup')}
                className="w-full text-center text-xs text-[#667085] hover:text-[#D0D5DD] transition-colors"
              >
                I don&apos;t use Plex — set up with Radarr
              </button>
            </>
          )}
        </div>
      </div>
    </div>
  )
}
