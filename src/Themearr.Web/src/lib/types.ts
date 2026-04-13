export interface Movie {
  id: string
  plexServerId: string
  plexRatingKey: string
  title: string
  year: number | null
  sourcePath: string | null
  folderName: string
  status: 'pending' | 'downloaded'
  posterUrl: string | null
}

export interface YoutubeResult {
  videoId: string
  title: string
  thumbnail: string | null
  duration: string | null
  channel: string
}

export interface PlexServer {
  id: string
  name: string
  url: string
  urls: string[]
  token: string
  owned: boolean
  presence: boolean
}

export interface PlexLibrary {
  key: string
  title: string
  type: string
}

export interface PathMapping {
  source: string
  target: string
}

export interface SetupStatus {
  setupComplete: boolean
  plexConnected: boolean
  plexAccountName: string
  selectedServers: PlexServer[]
  selectedLibraries: Record<string, string[]>
  pathMappings: PathMapping[]
  libraryPaths: string[]
}

export interface Settings {
  selectedServers: PlexServer[]
  selectedLibraries: Record<string, string[]>
  pathMappings: PathMapping[]
  libraryPaths: string[]
  advanced: {
    maxSearchDirs: number
    searchDepth: number
  }
}

export interface SyncStatus {
  inProgress: boolean
  finished: boolean
  error: string
  synced: number
  logs: string[]
}

export interface HistoryEntry {
  id: number
  movieId: string
  movieTitle: string
  movieYear: number | null
  themeTitle: string | null
  sourceUrl: string | null
  downloadedAt: string
}

export interface VersionInfo {
  current: string
  latest: string
  updateAvailable: boolean
  updating: boolean
  updateError: string
  checkError: string
  repo: string
}
