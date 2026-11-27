// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
import { Toaster as Sonner } from 'sonner'
import { useTheme } from '@/lib/theme'

export { toast } from 'sonner'

export function Toaster() {
  const { resolved } = useTheme()
  return (
    <Sonner
      theme={resolved}
      position="bottom-right"
      closeButton
      toastOptions={{
        classNames: {
          toast: 'group !rounded-lg !border !border-border !bg-popover !text-popover-foreground !shadow-lg',
          description: '!text-muted-foreground',
          // Its close button at least 24 pixels (WCAG 2.2 target size): Sonner's is 20.
          closeButton: '!size-6',
        },
      }}
    />
  )
}
