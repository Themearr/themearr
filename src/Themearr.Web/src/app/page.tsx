'use client'

import { useEffect } from 'react'
import { useRouter } from 'next/navigation'
import { useAuth } from '@/lib/auth'
import { Spinner } from '@/components/ui'

export default function RootPage() {
  const router = useRouter()
  const { loading, connected, setupComplete } = useAuth()

  useEffect(() => {
    if (loading) return
    if (!connected) router.replace('/login')
    else if (!setupComplete) router.replace('/setup')
    else router.replace('/movies')
  }, [loading, connected, setupComplete, router])

  return (
    <div className="flex min-h-screen items-center justify-center">
      <Spinner size={32} className="text-[#BB0000]" />
    </div>
  )
}
