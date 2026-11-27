// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
import { useSyncExternalStore } from 'react'

/** Whether a media query matches now, following changes (false where matchMedia is missing). */
export function useMedia(query: string): boolean {
  return useSyncExternalStore(
    (on) => {
      const mq = window.matchMedia?.(query)
      mq?.addEventListener('change', on)
      return () => mq?.removeEventListener('change', on)
    },
    () => !!window.matchMedia?.(query).matches,
    () => false,
  )
}
