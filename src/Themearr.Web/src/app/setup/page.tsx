import { useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import { useAuth } from '@/lib/auth'
import { SetupWizard } from '@/components/setup/SetupWizard'
import { Spinner } from '@/components/ui'

export default function SetupPage() {
  const navigate = useNavigate()
  const { loading, connected } = useAuth()

  useEffect(() => {
    if (!loading && !connected) navigate('/login', { replace: true })
  }, [loading, connected, navigate])

  if (loading) {
    return (
      <div className="flex min-h-screen items-center justify-center">
        <Spinner size={32} className="text-[#BB0000]" />
      </div>
    )
  }

  if (!connected) return null

  return (
    <div className="min-h-screen px-4 py-12">
      <SetupWizard />
    </div>
  )
}
