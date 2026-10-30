import type { Settings } from './types'

// These callers do not edit audio. Omitting the optional object keeps a late
// library/path/queue save from restoring processing the operator just disabled.
export function withoutAudioProcessing(settings: Settings): Settings {
  const payload = { ...settings }
  delete payload.audioProcessing
  return payload
}
