'use client'

import { type ButtonHTMLAttributes, type InputHTMLAttributes, type ReactNode, useEffect, useRef } from 'react'

// ── Badge ─────────────────────────────────────────────────────────────────────

type BadgeVariant = 'success' | 'warning' | 'error' | 'default'

const BADGE_STYLES: Record<BadgeVariant, string> = {
  success: 'bg-[#ECFDF3]/10 text-[#6CE9A6] border border-[#027A48]/40',
  warning: 'bg-[#FFFAEB]/10 text-[#FEC84B] border border-[#B54708]/40',
  error:   'bg-[#FEF3F2]/10 text-[#FDA29B] border border-[#B42318]/40',
  default: 'bg-[#1D2939] text-[#98A2B3] border border-[#344054]/60',
}

const BADGE_DOTS: Record<BadgeVariant, string> = {
  success: 'bg-[#12B76A]',
  warning: 'bg-[#F79009]',
  error:   'bg-[#F04438]',
  default: 'bg-[#667085]',
}

export function Badge({ variant = 'default', children }: { variant?: BadgeVariant; children: ReactNode }) {
  return (
    <span className={`inline-flex items-center gap-1.5 px-2 py-0.5 rounded-full text-xs font-medium ${BADGE_STYLES[variant]}`}>
      <span className={`w-1.5 h-1.5 rounded-full flex-shrink-0 ${BADGE_DOTS[variant]}`} />
      {children}
    </span>
  )
}

// ── Button ────────────────────────────────────────────────────────────────────

type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger'
type ButtonSize    = 'sm' | 'md' | 'lg'

const BTN_BASE = 'inline-flex items-center justify-center gap-2 rounded-lg font-semibold transition-all duration-150 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-offset-2 focus-visible:ring-offset-[#0C111D] disabled:opacity-50 disabled:cursor-not-allowed select-none'

const BTN_VARIANTS: Record<ButtonVariant, string> = {
  primary:   'bg-[#BB0000] hover:bg-[#990000] text-white shadow-sm focus-visible:ring-[#BB0000]',
  secondary: 'bg-[#1D2939] hover:bg-[#344054] text-[#D0D5DD] border border-[#344054] hover:border-[#475467] focus-visible:ring-[#475467]',
  ghost:     'bg-transparent hover:bg-[#1D2939] text-[#98A2B3] hover:text-[#F9FAFB] focus-visible:ring-[#475467]',
  danger:    'bg-[#D92D20] hover:bg-[#B42318] text-white shadow-sm focus-visible:ring-[#F04438]',
}

const BTN_SIZES: Record<ButtonSize, string> = {
  sm: 'px-3 py-1.5 text-xs h-8',
  md: 'px-3.5 py-2 text-sm h-9',
  lg: 'px-4 py-2.5 text-sm h-10',
}

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant
  size?: ButtonSize
  loading?: boolean
  children: ReactNode
}

export function Button({ variant = 'primary', size = 'md', loading, children, className = '', ...rest }: ButtonProps) {
  return (
    <button
      className={`${BTN_BASE} ${BTN_VARIANTS[variant]} ${BTN_SIZES[size]} ${className}`}
      disabled={loading || rest.disabled}
      {...rest}
    >
      {loading && <Spinner size={14} />}
      {children}
    </button>
  )
}

// ── Input ─────────────────────────────────────────────────────────────────────

interface InputProps extends InputHTMLAttributes<HTMLInputElement> {
  label?: string
  hint?: string
  error?: string
}

export function Input({ label, hint, error, className = '', ...rest }: InputProps) {
  return (
    <div className="flex flex-col gap-1.5">
      {label && (
        <label className="text-sm font-medium text-[#D0D5DD]">{label}</label>
      )}
      <input
        className={`
          w-full rounded-lg border bg-[#101828] px-3.5 py-2.5 text-sm text-[#F9FAFB]
          placeholder:text-[#475467] outline-none transition-all
          border-[#344054] focus:border-[#BB0000] focus:ring-1 focus:ring-[#BB0000]/40
          disabled:opacity-50 disabled:cursor-not-allowed
          ${error ? 'border-[#F04438] focus:border-[#F04438] focus:ring-[#F04438]/40' : ''}
          ${className}
        `}
        {...rest}
      />
      {hint && !error && <p className="text-xs text-[#667085]">{hint}</p>}
      {error && <p className="text-xs text-[#FDA29B]">{error}</p>}
    </div>
  )
}

// ── Spinner ───────────────────────────────────────────────────────────────────

export function Spinner({ size = 20, className = '' }: { size?: number; className?: string }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      className={`animate-spin ${className}`}
      aria-hidden
    >
      <circle cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="3" strokeOpacity="0.2" />
      <path
        d="M12 2a10 10 0 0 1 10 10"
        stroke="currentColor"
        strokeWidth="3"
        strokeLinecap="round"
      />
    </svg>
  )
}

// ── Modal ─────────────────────────────────────────────────────────────────────

interface ModalProps {
  open: boolean
  onClose: () => void
  title?: string
  children: ReactNode
  size?: 'sm' | 'md' | 'lg' | 'xl'
}

const MODAL_SIZES = {
  sm: 'max-w-sm',
  md: 'max-w-lg',
  lg: 'max-w-2xl',
  xl: 'max-w-4xl',
}

export function Modal({ open, onClose, title, children, size = 'md' }: ModalProps) {
  const backdropRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [open, onClose])

  if (!open) return null

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
      {/* backdrop */}
      <div
        ref={backdropRef}
        className="absolute inset-0 bg-black/60 backdrop-blur-sm"
        onClick={onClose}
      />
      {/* panel */}
      <div className={`relative z-10 w-full ${MODAL_SIZES[size]} rounded-xl border border-[#1D2939] bg-[#101828] shadow-2xl`}>
        {title && (
          <div className="flex items-center justify-between border-b border-[#1D2939] px-6 py-4">
            <h2 className="text-base font-semibold text-[#F9FAFB]">{title}</h2>
            <button
              onClick={onClose}
              className="rounded-lg p-1.5 text-[#667085] hover:bg-[#1D2939] hover:text-[#D0D5DD] transition-colors"
              aria-label="Close"
            >
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round">
                <path d="M18 6 6 18M6 6l12 12" />
              </svg>
            </button>
          </div>
        )}
        <div className="p-6">{children}</div>
      </div>
    </div>
  )
}

// ── Divider ───────────────────────────────────────────────────────────────────

export function Divider() {
  return <hr className="border-[#1D2939]" />
}

// ── Empty state ───────────────────────────────────────────────────────────────

export function EmptyState({ icon, title, description, action }: {
  icon?: ReactNode
  title: string
  description?: string
  action?: ReactNode
}) {
  return (
    <div className="flex flex-col items-center justify-center gap-3 py-16 text-center">
      {icon && <div className="mb-1 text-[#475467]">{icon}</div>}
      <p className="text-sm font-semibold text-[#D0D5DD]">{title}</p>
      {description && <p className="text-sm text-[#667085] max-w-xs">{description}</p>}
      {action && <div className="mt-2">{action}</div>}
    </div>
  )
}
