export type MediaKind = 'movies' | 'shows'
export type QueueCursor = { media: MediaKind; currentKey: string | null; visitedKeys: ReadonlySet<string> }
export const queueKey = (media: MediaKind, id: string): string => `${media}:${id}`

export function rebaseCursor(cursor: QueueCursor, previous: readonly string[], next: readonly string[]): QueueCursor {
  const available = new Set(next.filter(key => !cursor.visitedKeys.has(key)))
  if (cursor.currentKey && available.has(cursor.currentKey)) return cursor
  const oldIndex = cursor.currentKey ? previous.indexOf(cursor.currentKey) : -1
  const successors = oldIndex < 0 ? [] : [...previous.slice(oldIndex + 1), ...previous.slice(0, oldIndex)]
  const currentKey = successors.find(key => available.has(key)) ?? next.find(key => available.has(key)) ?? null
  return { ...cursor, currentKey }
}

export function advanceCursor(cursor: QueueCursor, orderedKeys: readonly string[], actedKey: string): QueueCursor {
  if (cursor.currentKey !== actedKey || !orderedKeys.includes(actedKey)) return cursor
  const visitedKeys = new Set(cursor.visitedKeys)
  visitedKeys.add(actedKey)
  return rebaseCursor({ ...cursor, visitedKeys }, orderedKeys, orderedKeys)
}

export function selectCursor(cursor: QueueCursor, orderedKeys: readonly string[], key: string): QueueCursor {
  const currentIndex = cursor.currentKey ? orderedKeys.indexOf(cursor.currentKey) : -1
  const traversal = currentIndex < 0 ? [...orderedKeys]
    : [...orderedKeys.slice(currentIndex), ...orderedKeys.slice(0, currentIndex)]
  const index = traversal.indexOf(key)
  if (index < 0 || cursor.visitedKeys.has(key)) return cursor
  const visitedKeys = new Set(cursor.visitedKeys)
  // Follow the displayed circular order, not a raw prefix of a reordered snapshot.
  for (const skipped of traversal.slice(0, index)) visitedKeys.add(skipped)
  return { ...cursor, currentKey: key, visitedKeys }
}
