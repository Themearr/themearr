import { useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import { useAuth } from '@/lib/auth'
import { SetupWizard } from '@/components/setup/SetupWizard'
import { Spinner } from '@/components/ui'

export default function SetupPage() {
  const navigate = useNavigate()
  // Reaching the wizard only requires a valid bearer token — Plex sign-in is a
  // property of the wizard's Plex branch, not a precondition for entering it.
  const { loading, authorized } = useAuth()

  useEffect(() => {
    if (!loading && !authorized) navigate('/login', { replace: true })
  }, [loading, authorized, navigate])

  if (loading) {
    return (
      <div className="flex min-h-screen items-center justify-center">
        <Spinner size={32} className="text-[#BB0000]" />
      </div>
    )
  }

  if (!authorized) return null

  return (
    <div className="min-h-screen px-4 py-12">
      <SetupWizard />
    </div>
  )
}
