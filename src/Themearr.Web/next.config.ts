import type { NextConfig } from 'next'

const config: NextConfig = {
  output: 'export',
  trailingSlash: true,
  // Static export — images don't need the Image Optimization API
  images: { unoptimized: true },
  // All API calls go to /api/* which is handled by the .NET backend
  // In dev, set NEXT_PUBLIC_API_URL=http://localhost:5000
}

export default config
