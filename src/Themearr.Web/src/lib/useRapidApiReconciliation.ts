import { useCallback, useEffect, useRef, useState } from 'react'
import { rapidApiApi } from './api'
import { boundedRequest } from './boundedRequest'

export function useRapidApiReconciliation() {
  const [rapidApiOk, setRapidApiOk] = useState<boolean | null>(null)
  const [rapidApiCheckError, setRapidApiCheckError] = useState('')
  const [rapidPhase, setRapidPhase] = useState<'checking' | 'unconfirmed' | 'confirmed'>('checking')
  const [rapidOrderChanged, setRapidOrderChanged] = useState(false)
  const rapidMutSeq = useRef(0)
  const activity = useRef(0)
  const reads = useRef(0)
  const pending = useRef(0)
  const mounted = useRef(false)
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)
  const uncertain = useRef(new Set<number>())
  const followUntil = useRef(0)
  const lastIntent = useRef<boolean | null>(null)

  const readRapidStatus = useCallback(async function read(revision: number): Promise<void> {
    if (!mounted.current || pending.current || revision !== activity.current) return
    const stamp = ++reads.current
    try {
      const status = await boundedRequest(signal => rapidApiApi.status({ signal }))
      if (!mounted.current || pending.current || revision !== activity.current || stamp !== reads.current) return
      setRapidApiOk(status.configured)
      setRapidApiCheckError('')
      setRapidOrderChanged(lastIntent.current !== null && lastIntent.current !== status.configured)
      setRapidPhase(uncertain.current.size ? 'unconfirmed' : 'confirmed')
    } catch (e) {
      if (!mounted.current || pending.current || revision !== activity.current || stamp !== reads.current) return
      setRapidApiCheckError((e as Error)?.message || 'Failed to check RapidAPI status.')
      setRapidPhase('unconfirmed')
    }
    if (mounted.current && !pending.current && revision === activity.current && stamp === reads.current
      && uncertain.current.size && Date.now() < followUntil.current) {
      timer.current = setTimeout(() => { if (Date.now() < followUntil.current) void read(revision) }, 1000)
    }
  }, [])

  const scheduleRapidReconciliation = useCallback(() => {
    clearTimeout(timer.current)
    if (!mounted.current || pending.current) return
    const revision = activity.current
    setRapidPhase(uncertain.current.size ? 'unconfirmed' : 'checking')
    timer.current = setTimeout(() => { void readRapidStatus(revision) }, 250)
  }, [readRapidStatus])

  const invalidateReads = useCallback(() => { activity.current++; reads.current++ }, [])

  useEffect(() => {
    mounted.current = true
    void readRapidStatus(activity.current)
    return () => { mounted.current = false; invalidateReads(); clearTimeout(timer.current) }
  }, [readRapidStatus, invalidateReads])

  function beginRapidMutation(desired: boolean): number {
    clearTimeout(timer.current)
    activity.current++; reads.current++; pending.current++
    lastIntent.current = desired
    setRapidPhase('checking')
    setRapidOrderChanged(false)
    setRapidApiCheckError('')
    return ++rapidMutSeq.current
  }

  function finishRapidMutation() {
    pending.current--
    scheduleRapidReconciliation()
  }

  async function waitRapidMutation<T>(revision: number, request: () => Promise<T>): Promise<T> {
    let settled = false
    let timedOut = false
    function observedSettlement() {
      settled = true
      if (!timedOut) return
      uncertain.current.delete(revision)
      // A late transport result can follow an apparently authoritative read.
      activity.current++; reads.current++
      scheduleRapidReconciliation()
    }
    const transport = Promise.resolve().then(request)
    void transport.then(observedSettlement, observedSettlement)
    try {
      return await boundedRequest(() => transport, 30000)
    } catch (e) {
      if (!settled) {
        timedOut = true
        uncertain.current.add(revision)
        followUntil.current = Date.now() + 30000
        if (mounted.current) setRapidPhase('unconfirmed')
      }
      throw e
    } finally { finishRapidMutation() }
  }

  async function checkRapidApiStatus(): Promise<void> {
    clearTimeout(timer.current)
    if (!mounted.current || pending.current) return
    setRapidPhase('checking')
    await readRapidStatus(activity.current)
  }

  return { rapidApiOk, setRapidApiOk, rapidApiCheckError, rapidPhase, rapidOrderChanged,
    rapidMutSeq, beginRapidMutation, waitRapidMutation, checkRapidApiStatus }
}
