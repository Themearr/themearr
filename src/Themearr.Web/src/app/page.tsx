import { useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import { useAuth } from '@/lib/auth'
import { Spinner } from '@/components/ui'

export default function RootPage() {
  const navigate = useNavigate()
  const { loading, connected, setupComplete } = useAuth()

  useEffect(() => {
    if (loading) return
    if (!connected) navigate('/login', { replace: true })
    else if (!setupComplete) navigate('/setup', { replace: true })
    else navigate('/dashboard', { replace: true })
  }, [loading, connected, setupComplete, navigate])

  return (
    <div className="flex min-h-screen items-center justify-center">
      <Spinner size={32} className="text-[#BB0000]" />
    </div>
  )
}
