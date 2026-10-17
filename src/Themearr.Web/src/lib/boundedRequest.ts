/** Abort fetch and release the caller even if a transport ignores cancellation. */
export async function boundedRequest<T>(request: (signal: AbortSignal) => Promise<T>, timeoutMs = 8000): Promise<T> {
  const controller = new AbortController()
  let timer: ReturnType<typeof setTimeout>
  const timeout = new Promise<never>((_, reject) => {
    timer = setTimeout(() => {
      controller.abort()
      reject(new Error('The request timed out. Please try again.'))
    }, timeoutMs)
  })
  try {
    return await Promise.race([Promise.resolve().then(() => request(controller.signal)), timeout])
  } finally {
    clearTimeout(timer!)
    controller.abort()
  }
}
