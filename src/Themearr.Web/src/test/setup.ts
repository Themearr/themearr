import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterEach } from 'vitest'

// Each test renders a page; without this, the previous test's DOM is still
// mounted and queries match the wrong render.
afterEach(cleanup)
