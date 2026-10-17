import { describe, expect, it } from 'vitest'
import { advanceCursor, queueKey, rebaseCursor, selectCursor, type QueueCursor } from './queueCursor'
const initial = (): QueueCursor => ({ media: 'movies', currentKey: 'movies:a', visitedKeys: new Set() })
const keys = ['movies:a', 'movies:b', 'movies:c']

describe('queue cursor transitions', () => {
  it('keeps the successor when a completed item vanishes', () => {
    const advanced = advanceCursor(initial(), keys, 'movies:a')
    expect(rebaseCursor(advanced, keys, ['movies:b', 'movies:c']).currentKey).toBe('movies:b')
  })
  it('retains an explicit selection through reordering without revisiting bypassed items', () => {
    const selected = selectCursor(initial(), keys, 'movies:b')
    const rebased = rebaseCursor(selected, keys, ['movies:b', 'movies:c', 'movies:a'])
    expect(rebased.currentKey).toBe('movies:b')
    expect([...rebased.visitedKeys]).toEqual(['movies:a'])
  })
  it('uses the next surviving successor if the current item disappears', () => {
    expect(rebaseCursor(initial(), keys, ['movies:c', 'movies:b']).currentKey).toBe('movies:b')
  })
  it('offers a new item after an exhausted snapshot', () => {
    const done = advanceCursor(initial(), ['movies:a'], 'movies:a')
    expect(done.currentKey).toBeNull()
    expect(rebaseCursor(done, ['movies:a'], ['movies:a', 'movies:d']).currentKey).toBe('movies:d')
  })
  it('does not advance on a missing or stale acted-on identity', () => {
    expect(advanceCursor(initial(), keys, 'movies:z')).toEqual(initial())
    expect(advanceCursor(initial(), keys, 'movies:b')).toEqual(initial())
  })
  it('qualifies colliding IDs by media and does not mutate prior state', () => {
    expect(queueKey('shows', 'a')).toBe('shows:a')
    const before = initial()
    advanceCursor(before, keys, 'movies:a')
    expect(before.visitedKeys.size).toBe(0)
    expect(before.currentKey).toBe('movies:a')
  })
  it('an Up next jump after reorder preserves wrapped unvisited items', () => {
    const current: QueueCursor = { media: 'movies', currentKey: 'movies:b', visitedKeys: new Set(['movies:a']) }
    const reordered = ['movies:c', 'movies:b', 'movies:d']
    const selected = selectCursor(current, reordered, 'movies:d')
    expect([...selected.visitedKeys]).toEqual(['movies:a', 'movies:b'])
    expect(advanceCursor(selected, reordered, 'movies:d').currentKey).toBe('movies:c')
  })

})
