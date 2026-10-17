import type { DownloadStatus } from './types'

export const LOST_DOWNLOAD_TRACKING = 'Lost tracking of this download attempt. Check its status before trying again.'

export function serverAttemptId(result: unknown): string | undefined {
  if (!result || typeof result !== 'object' || !('attemptId' in result)) return undefined
  return typeof result.attemptId === 'string' ? result.attemptId : undefined
}

export function lostDownloadAttempt(expected: string | undefined, status: DownloadStatus): boolean {
  if (!expected) return false
  // Omitted protocol fields retain compatibility with an older server.
  return status.known === false || status.state === 'unknown' || status.state === 'superseded'
    || (status.attemptId !== undefined && status.attemptId !== expected)
}
