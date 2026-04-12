'use client'

import { useEffect } from 'react'
import { useRouter } from 'next/navigation'
import { setupApi } from '@/lib/api'
import { Spinner } from '@/components/ui'

export default function RootPage() {
  const router = useRouter()

  useEffect(() => {
    setupApi.status()
      .then(s => router.replace(s.setupComplete ? '/movies' : '/setup'))
      .catch(() => router.replace('/setup'))
  }, [router])

  return (
    <div className="flex min-h-screen items-center justify-center">
      <Spinner size={32} className="text-[#7F56D9]" />
    </div>
  )
}
